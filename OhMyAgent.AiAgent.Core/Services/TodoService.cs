using System;
using System.Collections.Generic;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>ITodoService 기본 구현 — 락으로 보호되는 인메모리 목록(세션 수명, 영속 없음).</summary>
public sealed class TodoService : ITodoService
{
    private readonly object _lock = new();
    private IReadOnlyList<TodoItem> _current = Array.Empty<TodoItem>();

    public IReadOnlyList<TodoItem> Current
    {
        get { lock (_lock) return _current; }
    }

    public event EventHandler<IReadOnlyList<TodoItem>>? TodosChanged;

    public void Write(IReadOnlyList<TodoItem> todos)
    {
        var snapshot = todos ?? Array.Empty<TodoItem>();
        lock (_lock) _current = snapshot;
        TodosChanged?.Invoke(this, snapshot);
    }

    public void Clear() => Write(Array.Empty<TodoItem>());
}
