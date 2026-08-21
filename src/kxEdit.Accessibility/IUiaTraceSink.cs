namespace kxEdit.Accessibility;

/// <summary>
/// UIA イベント発火 (RaiseAutomationEvent / RaiseAutomationNotification) の失敗を診断可能にする
/// trace sink (UIA-L-2)。本番挙動は不変 — 既定は <c>null</c> で silent 継続 (SR は視覚のみに縮退)。
/// テストでは Fake を注入して「UIA raise が例外を投げても Trace に落ちる」ことを assert する。
/// </summary>
/// <remarks>
/// スコープは意図的に UIA だけに絞る。汎用 ITraceSink を作らないのは、バックアップ用の
/// <c>IBackupTraceSink</c> と同様にカテゴリを分離し、無関係な失敗を混線させないため。
/// </remarks>
public interface IUiaTraceSink
{
    /// <summary>
    /// 非致命な UIA 発火失敗を通知する。<paramref name="category"/> は
    /// "raise-automation-event" (<see cref="System.Windows.Automation.Provider.AutomationInteropProvider.RaiseAutomationEvent"/>)
    /// または "raise-automation-notification" (<see cref="System.Windows.Forms.AccessibleObject.RaiseAutomationNotification"/>) のいずれか。
    /// <paramref name="detail"/> は文脈 (event 種別など)。<paramref name="ex"/> は catch した例外。
    /// </summary>
    void Warn(string category, string detail, Exception ex);
}
