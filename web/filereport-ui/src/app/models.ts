export interface Identity {
  id: string;
  email: string;
  token: string;
  expiresAtUtc: string;
}
export interface InputFile {
  id: string;
  side: 'Baseline' | 'Candidate';
  name: string;
  bytes: string;
  sha256: string;
  expiresAtUtc: string;
}
export interface Attempt {
  number: number;
  failureCode: string | null;
  startedAtUtc: string;
  finishedAtUtc: string | null;
}
export interface AttemptMetric {
  attempt: number;
  elapsedSeconds: number;
  cpuSeconds: number;
  observedPeakWorkingSetBytes: string;
  observedPeakManagedHeapBytes: string;
  allocatedBytes: string;
  physicalReadBytes: string;
  physicalWrittenBytes: string;
  scratchPeakBytes: string;
  sampleIntervalMilliseconds: number;
  samples: number;
  complete: boolean;
}
export interface Measurements {
  uniqueInputBytes: string;
  uploadSeconds: number | null;
  submittedTotalSeconds: number | null;
  fullWorkflowSeconds: number | null;
  clockNote: string;
  memoryScope: string;
  availability: string;
  attempts: AttemptMetric[];
  cost: { status: string; total: number | null; reason: string };
}
export interface Job {
  id: string;
  revision: string;
  state: string;
  stage: string;
  files: InputFile[];
  serverReceivedBytes: string;
  hasReport: boolean;
  createdAtUtc: string;
  failureCode: string | null;
  attempts: Attempt[];
  measurements: Measurements;
  emailMode: string | null;
}
export interface Counts {
  added: string;
  removed: string;
  changed: string;
  unchanged: string;
}
export interface Report {
  baselineRecords: string;
  candidateRecords: string;
  counts: Counts;
  comparedColumns: string[];
  samplesTruncated: boolean;
  artifact: { id: string; bytes: string; expiresAtUtc: string };
}
export interface Difference {
  kind: string;
  key: string[];
  baseline: string[] | null;
  candidate: string[] | null;
}
export interface SamplePage {
  items: Difference[];
  retainedCount: number;
  samplesTruncated: boolean;
  nextOffset: number | null;
}
export interface HistoryPage {
  items: Job[];
  nextCursor: string | null;
}
export interface Delivery {
  id: string;
  state: string;
  recipient: string;
  errorCode: string | null;
}
export interface JobEvent {
  jobId: string;
  revision: string;
  state: string;
}
