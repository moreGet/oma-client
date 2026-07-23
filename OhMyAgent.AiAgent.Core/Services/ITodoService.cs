using System;
using System.Collections.Generic;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// 에이전트 작업 계획(todo) 공유 저장소. manage_todos 도구가 갱신하고 ViewModel이 구독해 UI에 반영한다.
/// </summary>
public interface ITodoService
{
    IReadOnlyList<TodoItem> Current { get; }

    /// <summary>목록이 교체될 때마다 발화(백그라운드 스레드일 수 있음 — 구독자가 UI 마샬 책임).</summary>
    event EventHandler<IReadOnlyList<TodoItem>>? TodosChanged;

    /// <summary>전체 목록을 교체(부분 갱신 아님)하고 TodosChanged 발화.</summary>
    void Write(IReadOnlyList<TodoItem> todos);

    /// <summary>목록을 비운다(새 목표/로그아웃 시).</summary>
    void Clear();
}
