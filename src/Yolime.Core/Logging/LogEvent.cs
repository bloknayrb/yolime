namespace Yolime.Core.Logging;

public enum LogEvent
{
    AppStarted,
    RunStarted,
    StageCompleted,
    StageFailed,
    RunFinished,

    RunRejectedBusy,
    UnhandledException,
    SettingsCorruptBackedUp,
    SettingsMigrated,
    SettingsSaveFailed,
    HistoryNewerSchema,
    HistoryMigrated,
    HistoryWriteFailed,
    HistoryDeleteFailed,
    PendingPruned,
    HistoryPruned,
    ClipboardListenerFailed,
    OutputNotPasted,
    ClipboardRestored,
    ConnectionWarmed,
    HoldDiscarded,
    ForegroundSettled,
    StreamFellBack,
    StreamAborted,
    StreamParked,
    ClipboardRestoreSkipped,
    DelayedRenderUnavailable,
    UpdateChecked,
    UpdateCheckFailed,
    UpdateDownloaded,
    UpdateDownloadFailed,
    UpdateInstallStarted,
    UpdateInstallFailed,
    UpdateInstalled,

    /// <summary>A spoken command picked the run's mode. Its only field is the mode, as a tag.</summary>
    ModeCommand,
}
