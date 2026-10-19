namespace WhatDo.StatusService.Status;

public enum StatusAction
{
    UpdateStatus,
    RemoveInstance,
    Ignore
}

public sealed record StatusMapResult(StatusAction Action, HookEventStatus? Status = null)
{
    public static readonly StatusMapResult Ignored = new(StatusAction.Ignore);
    public static readonly StatusMapResult Removed = new(StatusAction.RemoveInstance);

    public static StatusMapResult Update(HookEventStatus status) => new(StatusAction.UpdateStatus, status);
}

public static class StatusMapper
{
    public static StatusMapResult Map(HookEventPayload payload) => payload.HookEventName switch
    {
        "SessionStart" => StatusMapResult.Update(HookEventStatus.Idle),
        "UserPromptSubmit" => StatusMapResult.Update(HookEventStatus.Working),
        "PreToolUse" => StatusMapResult.Update(HookEventStatus.Working),
        "PostToolUse" => StatusMapResult.Update(HookEventStatus.Working),
        "PostToolUseFailure" => StatusMapResult.Update(HookEventStatus.Working),
        "Stop" => StatusMapResult.Update(HookEventStatus.Idle),
        "SessionEnd" => StatusMapResult.Removed,
        "Notification" => MapNotification(payload.NotificationType),
        _ => StatusMapResult.Ignored
    };

    private static StatusMapResult MapNotification(string? notificationType) => notificationType switch
    {
        "permission_prompt" or "elicitation_dialog" => StatusMapResult.Update(HookEventStatus.PendingInput),
        "agent_idle" or "shell_completed" or "agent_completed" or "shell_detached_completed"
            => StatusMapResult.Update(HookEventStatus.Idle),
        _ => StatusMapResult.Ignored
    };
}
