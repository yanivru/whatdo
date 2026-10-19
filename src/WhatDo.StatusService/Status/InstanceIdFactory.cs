using System.Text.RegularExpressions;

namespace WhatDo.StatusService.Status;

public static class InstanceIdFactory
{
    public static string CreateName(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd))
        {
            return "unknown";
        }

        var trimmed = cwd.TrimEnd('/', '\\');
        var folder = trimmed
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault();

        if (string.IsNullOrWhiteSpace(folder))
        {
            return "unknown";
        }

        var sanitized = Regex.Replace(folder.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrEmpty(sanitized) ? "unknown" : sanitized;
    }

    public static string CreateShortSessionId(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return "unknown";
        }

        return sessionId.Length <= 8 ? sessionId : sessionId[..8];
    }

    public static string CreateInstanceId(string? cwd, string? sessionId)
        => $"{CreateName(cwd)}-{CreateShortSessionId(sessionId)}";
}
