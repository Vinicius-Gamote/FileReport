import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { App } from './app';
import { Session } from './session';
import { Workspace } from './workspace';
import { Job } from './models';

describe('Authenticated workspace', () => {
  let http: HttpTestingController;
  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [App],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());
  it('requires authentication and does not claim unmeasured performance', () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    const text = (fixture.nativeElement as HTMLElement).textContent;
    expect(text).toContain('No capacity or speed is promised before measurement.');
    expect(text).toContain('Sign in');
    expect((fixture.nativeElement as HTMLElement).querySelector('input[type=file]')).toBeNull();
  });
  it('registers directly without an account-confirmation step and keeps tokens out of storage', async () => {
    const session = TestBed.inject(Session);
    const pending = session.authenticate('register', 'user@example.test', 'SyntheticPassword12');
    http.expectOne('/api/v1/auth/register').flush({
      id: 'id',
      email: 'user@example.test',
      token: 'test-token',
      expiresAtUtc: new Date(Date.now() + 60000).toISOString(),
    });
    await pending;
    expect(session.identity()?.email).toBe('user@example.test');
    expect(localStorage.length).toBe(0);
    expect(sessionStorage.length).toBe(0);
    session.logout();
    expect(session.identity()).toBeNull();
  });
  it('ignores stale revisions without losing integer precision', () => {
    const workspace = TestBed.inject(Workspace);
    workspace.accept({ id: 'one', revision: '9007199254740993', state: 'Succeeded' } as Job);
    workspace.accept({ id: 'one', revision: '9007199254740992', state: 'Processing' } as Job);
    expect(workspace.job()?.state).toBe('Succeeded');
    const fixture = TestBed.createComponent(App);
    expect(fixture.componentInstance.format('9007199254740993')).toBe('9,007,199,254,740,993');
    expect(fixture.componentInstance.format(null)).toBe('Unavailable');
  });
  it('prevents file submission before keys and two stored files exist', () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    TestBed.inject(Session).identity.set({
      id: 'id',
      email: 'user@example.test',
      token: 'token',
      expiresAtUtc: new Date(Date.now() + 60000).toISOString(),
    });
    fixture.detectChanges();
    const start = Array.from(
      (fixture.nativeElement as HTMLElement).querySelectorAll('button'),
    ).find((b) => b.textContent?.includes('Start comparison'));
    expect(start?.disabled).toBe(true);
  });
});
