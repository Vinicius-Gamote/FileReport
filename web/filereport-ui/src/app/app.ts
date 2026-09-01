import { Component, effect, inject, OnDestroy, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { ComparisonChart } from './comparison-chart';
import { Session } from './session';
import { Workspace } from './workspace';
import { Delivery, HistoryPage, Report, SamplePage } from './models';

@Component({
  selector: 'app-root',
  imports: [
    CommonModule,
    FormsModule,
    MatButtonModule,
    MatCardModule,
    MatProgressBarModule,
    MatInputModule,
    MatSelectModule,
    ComparisonChart,
  ],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App implements OnDestroy {
  readonly session = inject(Session);
  readonly workspace = inject(Workspace);
  readonly busy = signal(false);
  readonly error = signal('');
  readonly report = signal<Report | null>(null);
  readonly samples = signal<SamplePage | null>(null);
  readonly history = signal<HistoryPage>({ items: [], nextCursor: null });
  readonly delivery = signal<Delivery | null>(null);
  readonly headers = signal<string[]>([]);
  mode: 'login' | 'register' = 'login';
  email = '';
  password = '';
  keys: string[] = [];
  columns: string[] = [];
  baselineDelimiter = ',';
  candidateDelimiter = ',';
  baselineEncoding = 'Utf8';
  candidateEncoding = 'Utf8';
  allColumns = true;
  readonly delimiters = [
    { label: 'Comma', value: ',' },
    { label: 'Semicolon', value: ';' },
    { label: 'Tab', value: '\t' },
  ];
  readonly encodings = [
    { label: 'UTF-8', value: 'Utf8' },
    { label: 'Windows-1252 (Excel / legacy)', value: 'Windows1252' },
    { label: 'UTF-16 little-endian', value: 'Utf16LittleEndian' },
    { label: 'UTF-16 big-endian', value: 'Utf16BigEndian' },
  ];
  private reportJob: string | null = null;
  private timer = setInterval(() => {
    if (this.session.identity()) void this.poll();
  }, 5000);
  constructor() {
    effect(() => {
      const job = this.workspace.job();
      if (job?.hasReport && this.reportJob !== job.id) {
        this.reportJob = job.id;
        void this.loadReport();
      }
      if (!this.session.identity()) {
        void this.workspace.reset();
        this.report.set(null);
        this.history.set({ items: [], nextCursor: null });
        this.delivery.set(null);
      }
    });
  }
  async run(action: () => Promise<void>): Promise<void> {
    if (this.busy()) return;
    this.busy.set(true);
    this.error.set('');
    try {
      await action();
    } catch (error) {
      this.handle(error);
    } finally {
      this.busy.set(false);
    }
  }
  private handle(error: unknown): void {
    if (error instanceof HttpErrorResponse && error.status === 401) this.session.logout();
    this.error.set(
      error instanceof HttpErrorResponse
        ? (error.error?.title ?? 'The service is unavailable. Retry the operation.')
        : error instanceof Error
          ? error.message
          : 'The operation failed.',
    );
  }
  authenticate(): void {
    void this.run(async () => {
      await this.session.authenticate(this.mode, this.email, this.password);
      this.password = '';
      await this.loadHistory();
      const id = new URLSearchParams(location.search).get('job');
      if (id) await this.workspace.open(id);
    });
  }
  selected(event: Event, side: string): void {
    const file = (event.target as HTMLInputElement).files?.[0];
    if (!file) return;
    void this.run(async () => {
      await this.workspace.select(side, file);
      await this.preview();
    });
  }
  async preview(): Promise<void> {
    if (this.workspace.job()?.files.length !== 2) return;
    const result = await this.workspace.headers(
      this.baselineDelimiter,
      this.candidateDelimiter,
      this.baselineEncoding,
      this.candidateEncoding,
    );
    this.headers.set(result['Baseline'] ?? []);
    this.keys = this.keys.filter((k) => this.headers().includes(k));
    this.columns = this.columns.filter((k) => this.headers().includes(k) && !this.keys.includes(k));
  }
  submit(): void {
    void this.run(async () => {
      await this.workspace.submit(
        this.keys,
        this.allColumns ? null : this.columns,
        this.baselineDelimiter,
        this.candidateDelimiter,
        this.baselineEncoding,
        this.candidateEncoding,
      );
      await this.loadHistory();
    });
  }
  async poll(): Promise<void> {
    try {
      await this.workspace.refresh();
      const delivery = this.delivery();
      if (delivery && ['Pending', 'Sending'].includes(delivery.state))
        this.delivery.set(await this.workspace.delivery(delivery.id));
    } catch (error) {
      this.handle(error);
    }
  }
  async loadHistory(cursor: string | null = null): Promise<void> {
    this.history.set(await this.workspace.history(cursor));
  }
  open(id: string): void {
    void this.run(async () => {
      this.report.set(null);
      this.samples.set(null);
      this.delivery.set(null);
      this.reportJob = null;
      await this.workspace.open(id);
    });
  }
  newComparison(): void {
    void this.run(async () => {
      await this.workspace.reset();
      this.report.set(null);
      this.samples.set(null);
      this.delivery.set(null);
      this.reportJob = null;
      this.headers.set([]);
      this.keys = [];
      this.columns = [];
    });
  }
  async loadReport(): Promise<void> {
    try {
      this.report.set((await this.workspace.report()).report);
      this.samples.set(await this.workspace.samples());
      await this.loadHistory();
    } catch (error) {
      this.reportJob = null;
      this.handle(error);
    }
  }
  nextSamples(): void {
    void this.run(async () =>
      this.samples.set(await this.workspace.samples(this.samples()?.nextOffset ?? 0)),
    );
  }
  sendEmail(): void {
    void this.run(async () => this.delivery.set(await this.workspace.email()));
  }
  download(): void {
    const report = this.report();
    if (report) void this.run(() => this.workspace.download(report));
  }
  isEditable(): boolean {
    return (
      !this.workspace.job() || ['Draft', 'Uploading', 'Ready'].includes(this.workspace.job()!.state)
    );
  }
  format(value: string | null | undefined): string {
    return value == null ? 'Unavailable' : BigInt(value).toLocaleString('en-US');
  }
  seconds(value: number | null | undefined): string {
    return value == null ? 'Unavailable' : value.toFixed(3) + ' s';
  }
  ngOnDestroy(): void {
    clearInterval(this.timer);
    void this.workspace.reset();
  }
}
