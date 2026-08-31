import { Injectable, inject, signal } from '@angular/core';
import { HttpClient, HttpHeaders } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { Identity } from './models';
@Injectable({ providedIn: 'root' })
export class Session {
  private http = inject(HttpClient);
  readonly identity = signal<Identity | null>(null);
  private expiry: ReturnType<typeof setTimeout> | undefined;
  async authenticate(mode: 'register' | 'login', email: string, password: string): Promise<void> {
    const user = await firstValueFrom(
      this.http.post<Identity>('/api/v1/auth/' + mode, { email, password }),
    );
    this.identity.set(user);
    clearTimeout(this.expiry);
    this.expiry = setTimeout(
      () => this.logout(),
      Math.max(0, Date.parse(user.expiresAtUtc) - Date.now()),
    );
  }
  logout(): void {
    clearTimeout(this.expiry);
    this.identity.set(null);
  }
  headers(extra: Record<string, string> = {}): HttpHeaders {
    const user = this.identity();
    if (!user || Date.parse(user.expiresAtUtc) <= Date.now()) {
      this.logout();
      throw new Error('Your session expired. Sign in again.');
    }
    return new HttpHeaders({ Authorization: 'Bearer ' + user.token, ...extra });
  }
}
