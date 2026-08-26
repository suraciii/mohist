export interface PendingUpdateWork {
  readonly ownerKind: string;
  readonly ownerId: string;
  readonly workId: string;
  readonly taskRunId?: string | null;
  readonly workType: string;
  readonly status?: string | null;
}

export interface PendingUpdateOperation {
  readonly operationId: string;
  readonly runnerId?: string | null;
  readonly createdAt: string;
  readonly affectedWorks: readonly PendingUpdateWork[];
}
