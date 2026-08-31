import { Injectable, inject, signal } from '@angular/core';
import { HttpClient, HttpEventType } from '@angular/common/http';
import { firstValueFrom, timeout } from 'rxjs';
import { HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { Delivery, HistoryPage, Job, JobEvent, Report, SamplePage } from './models';
import { Session } from './session';
@Injectable({ providedIn: 'root' })
export class Workspace {
  private http = inject(HttpClient);
  readonly session = inject(Session);
  readonly job = signal<Job | null>(null);
  readonly connection = signal('Disconnected — polling available');
  readonly transfer = signal(0);
  private hub: HubConnection | undefined;
  private hubStart: Promise<void> | undefined;
  private createKey = crypto.randomUUID();
  private submitRequest: { id: string; revision: string; key: string } | undefined;
  private emailKey = crypto.randomUUID();
  private refreshing = false;
  accept(job: Job): void {
    const current = this.job();
    if (!current || current.id !== job.id || BigInt(job.revision) >= BigInt(current.revision))
      this.job.set(job);
  }
  async connect(): Promise<void> {
    if (this.hubStart) return this.hubStart;
    this.hub = new HubConnectionBuilder()
      .withUrl('/hubs/comparisons', {
        accessTokenFactory: () => this.session.identity()?.token ?? '',
      })
      .withAutomaticReconnect([0, 2000, 5000, 10000])
      .configureLogging(LogLevel.None)
      .build();
    this.hub.on('JobUpdated.v1', (event: JobEvent) => {
      const current = this.job();
      if (current?.id === event.jobId && BigInt(event.revision) > BigInt(current.revision))
        void this.refresh().catch(() => {});
    });
    this.hub.onreconnecting(() => this.connection.set('Reconnecting — polling active'));
    this.hub.onclose(() => {
      this.hubStart = undefined;
      this.connection.set('Disconnected — polling active');
    });
    this.hub.onreconnected(() => {
      void this.subscribe().catch(() => this.connection.set('Polling active'));
    });
    this.hubStart = this.hub
      .start()
      .then(() => {
        this.connection.set('Live updates connected');
      })
      .catch(() => {
        this.hubStart = undefined;
        this.connection.set('Live updates unavailable — polling active');
      });
    return this.hubStart;
  }
  private async subscribe(): Promise<void> {
    if (this.hub?.state === 'Connected' && this.job()) {
      this.accept(await this.hub.invoke<Job>('Subscribe', this.job()!.id));
      this.connection.set('Live updates connected');
    }
  }
  async select(side: string, file: File): Promise<void> {
    // Start SignalR at selection; bounded connection wait lets HTTP uploads work during hub outages.
    const connection = this.connect();
    if (!this.job())
      this.accept(
        await firstValueFrom(
          this.http.post<Job>(
            '/api/v1/comparisons',
            {},
            { headers: this.session.headers({ 'Idempotency-Key': this.createKey }) },
          ),
        ),
      );
    await Promise.race([connection, new Promise<void>((resolve) => setTimeout(resolve, 5000))]);
    await this.subscribe();
    const job = this.job()!;
    const form = new FormData();
    form.append('file', file, file.name);
    this.transfer.set(0);
    await new Promise<void>((resolve, reject) =>
      this.http
        .put<Job>(`/api/v1/comparisons/${job.id}/files/${side}`, form, {
          headers: this.session.headers({ 'If-Match': job.revision }),
          observe: 'events',
          reportProgress: true,
        })
        .subscribe({
          next: (event) => {
            if (event.type === HttpEventType.UploadProgress)
              this.transfer.set(event.total ? Math.round((event.loaded * 100) / event.total) : 0);
            if (event.type === HttpEventType.Response && event.body) {
              this.accept(event.body);
              resolve();
            }
          },
          error: reject,
        }),
    );
  }
  async headers(baseline: string, candidate: string): Promise<Record<string, string[]>> {
    return firstValueFrom(
      this.http.get<Record<string, string[]>>(`/api/v1/comparisons/${this.job()!.id}/schema`, {
        headers: this.session.headers(),
        params: { baselineDelimiter: baseline, candidateDelimiter: candidate },
      }),
    );
  }
  async submit(
    keys: string[],
    columns: string[] | null,
    baseline: string,
    candidate: string,
  ): Promise<void> {
    if (!this.submitRequest) {
      const job = this.job()!;
      this.accept(
        await firstValueFrom(
          this.http.patch<Job>(
            `/api/v1/comparisons/${job.id}/options`,
            { keys, columns, baselineDelimiter: baseline, candidateDelimiter: candidate },
            { headers: this.session.headers({ 'If-Match': job.revision }) },
          ),
        ),
      );
      this.submitRequest = { id: job.id, revision: this.job()!.revision, key: crypto.randomUUID() };
    }
    const request = this.submitRequest;
    this.accept(
      await firstValueFrom(
        this.http.post<Job>(
          `/api/v1/comparisons/${request.id}/submit`,
          {},
          {
            headers: this.session.headers({
              'If-Match': request.revision,
              'Idempotency-Key': request.key,
            }),
          },
        ),
      ),
    );
  }
  async refresh(): Promise<void> {
    const id = this.job()?.id;
    if (!id || this.refreshing) return;
    this.refreshing = true;
    try {
      const job = await firstValueFrom(
        this.http
          .get<Job>('/api/v1/comparisons/' + id, { headers: this.session.headers() })
          .pipe(timeout(10000)),
      );
      if (this.job()?.id === id) this.accept(job);
    } finally {
      this.refreshing = false;
    }
  }
  async open(id: string): Promise<void> {
    await this.reset();
    this.accept(
      await firstValueFrom(
        this.http.get<Job>('/api/v1/comparisons/' + id, { headers: this.session.headers() }),
      ),
    );
    await this.connect();
    await this.subscribe();
  }
  history(cursor: string | null = null): Promise<HistoryPage> {
    return firstValueFrom(
      this.http.get<HistoryPage>('/api/v1/comparisons', {
        headers: this.session.headers(),
        params: cursor ? { cursor } : {},
      }),
    );
  }
  report(): Promise<{ report: Report }> {
    return firstValueFrom(
      this.http.get<{ report: Report }>(`/api/v1/comparisons/${this.job()!.id}/report`, {
        headers: this.session.headers(),
      }),
    );
  }
  samples(offset = 0): Promise<SamplePage> {
    return firstValueFrom(
      this.http.get<SamplePage>(`/api/v1/comparisons/${this.job()!.id}/samples`, {
        headers: this.session.headers(),
        params: { offset, limit: 20 },
      }),
    );
  }
  email(): Promise<Delivery> {
    return firstValueFrom(
      this.http.post<Delivery>(
        `/api/v1/comparisons/${this.job()!.id}/email`,
        {},
        { headers: this.session.headers({ 'Idempotency-Key': this.emailKey }) },
      ),
    );
  }
  delivery(id: string): Promise<Delivery> {
    return firstValueFrom(
      this.http.get<Delivery>('/api/v1/email-deliveries/' + id, {
        headers: this.session.headers(),
      }),
    );
  }
  async download(report: Report): Promise<void> {
    type SavePicker = {
      showSaveFilePicker?: (
        options: object,
      ) => Promise<{ createWritable(): Promise<WritableStream<Uint8Array>> }>;
    };
    const picker = (window as unknown as SavePicker).showSaveFilePicker;
    const target = picker ? await picker({ suggestedName: 'comparison.jsonl' }) : null;
    if (!target && BigInt(report.artifact.bytes) > 16777216n)
      throw new Error(
        'This browser cannot stream large downloads. Use a browser with Save File support or the authenticated API.',
      );
    const response = await fetch(
      `/api/v1/comparisons/${this.job()!.id}/artifacts/${report.artifact.id}`,
      { headers: { Authorization: 'Bearer ' + this.session.identity()!.token } },
    );
    if (!response.ok || !response.body)
      throw new Error(response.status === 410 ? 'The artifact has expired.' : 'Download failed.');
    if (target) await response.body.pipeTo(await target.createWritable());
    else {
      const url = URL.createObjectURL(await response.blob());
      const link = document.createElement('a');
      link.href = url;
      link.download = 'comparison.jsonl';
      link.click();
      setTimeout(() => URL.revokeObjectURL(url), 1000);
    }
  }
  async reset(): Promise<void> {
    this.job.set(null);
    this.submitRequest = undefined;
    this.createKey = crypto.randomUUID();
    this.emailKey = crypto.randomUUID();
    this.transfer.set(0);
    const hub = this.hub;
    this.hub = undefined;
    this.hubStart = undefined;
    await hub?.stop();
  }
}
