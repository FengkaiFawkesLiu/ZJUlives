namespace ZJUSS.Core.Models;

public enum SourceId
{
    Jwgl,
    Xzzd
}

public enum AssessmentType
{
    Homework,
    Exam
}

public enum SourceSyncStatus
{
    NeverSynced,
    Syncing,
    Updated,
    Empty,
    NotUpdated,
    TimedOut
}

public enum SourceFailureReason
{
    Unreachable,
    Timeout,
    Maintenance,
    LoginFailed,
    VerificationRequired,
    SchemaChanged,
    DataCorrupted,
    StorageFailed,
    Unknown
}
