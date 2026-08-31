import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { timeout } from 'rxjs';

export interface SystemStatus {
  application: string;
  stage: string;
  canAuthenticate: boolean;
  canSubmitComparisons: boolean;
  canSendEmail: boolean;
  measurementStatus: string;
}

@Injectable({ providedIn: 'root' })
export class SystemStatusApi {
  private readonly http = inject(HttpClient);
  getStatus() {
    return this.http.get<SystemStatus>('/api/v1/system').pipe(timeout(5000));
  }
}
