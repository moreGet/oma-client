using System;

namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>사이드바 프로젝트 목록용 헤더 정보. ConversationCount는 세션 group-by로 산출.</summary>
public sealed record ProjectSummary(
    string Id,
    string Name,
    int ConversationCount,
    DateTimeOffset UpdatedUtc,
    bool Synced);
