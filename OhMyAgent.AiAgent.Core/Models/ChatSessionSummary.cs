using System;

namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>사이드바 채팅 목록용 헤더 정보(메시지 본문 제외).</summary>
public sealed record ChatSessionSummary(
    string Id,
    string Title,
    DateTimeOffset UpdatedUtc,
    string? WorkspaceRoot,
    int MessageCount,
    string? ProjectId = null);
