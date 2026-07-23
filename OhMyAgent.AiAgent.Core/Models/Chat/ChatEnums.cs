namespace OhMyAgent.AiAgent.Client.Models.Chat;

/// <summary>방 유형. 와이어: "direct" | "group".</summary>
public enum ChatRoomType { Direct, Group }

/// <summary>타이핑 상태. 와이어: "start" | "stop".</summary>
public enum TypingState { Start, Stop }

/// <summary>WS 연결 상태(UI 표시용).</summary>
public enum ChatConnectionState { Disconnected, Connecting, Connected, Reconnecting }

/// <summary>메시지 전송 상태(클라 전용 — 와이어 미전송).</summary>
public enum ChatSendStatus { Pending, Sent, Failed }
