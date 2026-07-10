"""Small PostgreSQL persistence layer for the SpeakUp prototype.

The app is still a local competition prototype, so this keeps the API compact:
users, sessions, agent messages, and latest report snapshots.
"""

from __future__ import annotations

import hashlib
import os
import secrets
import uuid
from contextlib import contextmanager
from datetime import datetime, timezone
from typing import Any, Iterator

import psycopg
from psycopg.rows import dict_row
from psycopg.types.json import Jsonb

DATABASE_URL = os.environ.get("DATABASE_URL")


class DatabaseUnavailable(RuntimeError):
    pass


def _require_database_url() -> str:
    if not DATABASE_URL:
        raise DatabaseUnavailable("DATABASE_URL is not configured")
    return DATABASE_URL


@contextmanager
def get_conn() -> Iterator[psycopg.Connection]:
    conn = psycopg.connect(_require_database_url(), row_factory=dict_row)
    try:
        yield conn
        conn.commit()
    except Exception:
        conn.rollback()
        raise
    finally:
        conn.close()


def now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


def new_id(prefix: str) -> str:
    return f"{prefix}_{uuid.uuid4().hex[:18]}"


def hash_password(password: str) -> str:
    salt = secrets.token_hex(16)
    digest = hashlib.sha256(f"{salt}:{password}".encode("utf-8")).hexdigest()
    return f"{salt}${digest}"


def verify_password(password: str, stored: str) -> bool:
    try:
        salt, expected = stored.split("$", 1)
    except ValueError:
        return False
    digest = hashlib.sha256(f"{salt}:{password}".encode("utf-8")).hexdigest()
    return secrets.compare_digest(digest, expected)


def init_db() -> None:
    if not DATABASE_URL:
        return
    with get_conn() as conn:
        conn.execute(
            """
            CREATE TABLE IF NOT EXISTS users (
              id TEXT PRIMARY KEY,
              email TEXT UNIQUE NOT NULL,
              display_name TEXT NOT NULL,
              password_hash TEXT NOT NULL,
              created_at TIMESTAMPTZ NOT NULL DEFAULT now()
            )
            """
        )
        conn.execute(
            """
            CREATE TABLE IF NOT EXISTS sessions (
              id TEXT PRIMARY KEY,
              user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
              title TEXT NOT NULL,
              scenario TEXT NOT NULL,
              situation TEXT NOT NULL,
              focus_goals JSONB NOT NULL DEFAULT '[]'::jsonb,
              source TEXT NOT NULL DEFAULT 'live',
              status TEXT NOT NULL DEFAULT 'ready',
              last_report JSONB,
              created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
              updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
            )
            """
        )
        conn.execute(
            """
            CREATE TABLE IF NOT EXISTS agent_messages (
              id TEXT PRIMARY KEY,
              session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
              role TEXT NOT NULL,
              content TEXT NOT NULL,
              t DOUBLE PRECISION,
              metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
              created_at TIMESTAMPTZ NOT NULL DEFAULT now()
            )
            """
        )
        conn.execute("CREATE INDEX IF NOT EXISTS idx_sessions_user_id ON sessions(user_id, updated_at DESC)")
        conn.execute("CREATE INDEX IF NOT EXISTS idx_agent_messages_session_id ON agent_messages(session_id, created_at)")


def user_public(row: dict[str, Any]) -> dict[str, Any]:
    return {
        "id": row["id"],
        "email": row["email"],
        "display_name": row["display_name"],
        "created_at": row["created_at"].isoformat() if hasattr(row["created_at"], "isoformat") else row["created_at"],
    }


def session_public(row: dict[str, Any]) -> dict[str, Any]:
    return {
        "id": row["id"],
        "user_id": row["user_id"],
        "title": row["title"],
        "scenario": row["scenario"],
        "situation": row["situation"],
        "focus_goals": row["focus_goals"] or [],
        "source": row["source"],
        "status": row["status"],
        "last_report": row["last_report"],
        "created_at": row["created_at"].isoformat() if hasattr(row["created_at"], "isoformat") else row["created_at"],
        "updated_at": row["updated_at"].isoformat() if hasattr(row["updated_at"], "isoformat") else row["updated_at"],
    }


def message_public(row: dict[str, Any]) -> dict[str, Any]:
    return {
        "id": row["id"],
        "session_id": row["session_id"],
        "role": row["role"],
        "content": row["content"],
        "t": row["t"],
        "metadata": row["metadata"] or {},
        "created_at": row["created_at"].isoformat() if hasattr(row["created_at"], "isoformat") else row["created_at"],
    }


def create_user(email: str, password: str, display_name: str) -> dict[str, Any]:
    with get_conn() as conn:
        row = conn.execute(
            """
            INSERT INTO users (id, email, display_name, password_hash)
            VALUES (%s, %s, %s, %s)
            RETURNING id, email, display_name, created_at
            """,
            (new_id("user"), email.lower().strip(), display_name.strip(), hash_password(password)),
        ).fetchone()
    return user_public(row)


def authenticate_user(email: str, password: str) -> dict[str, Any] | None:
    with get_conn() as conn:
        row = conn.execute(
            "SELECT id, email, display_name, password_hash, created_at FROM users WHERE email = %s",
            (email.lower().strip(),),
        ).fetchone()
    if not row or not verify_password(password, row["password_hash"]):
        return None
    return user_public(row)


def upsert_session(payload: dict[str, Any]) -> dict[str, Any]:
    session_id = payload.get("id") or new_id("sess")
    with get_conn() as conn:
        row = conn.execute(
            """
            INSERT INTO sessions (id, user_id, title, scenario, situation, focus_goals, source, status, last_report)
            VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s)
            ON CONFLICT (id) DO UPDATE SET
              title = EXCLUDED.title,
              scenario = EXCLUDED.scenario,
              situation = EXCLUDED.situation,
              focus_goals = EXCLUDED.focus_goals,
              source = EXCLUDED.source,
              status = EXCLUDED.status,
              last_report = COALESCE(EXCLUDED.last_report, sessions.last_report),
              updated_at = now()
            RETURNING *
            """,
            (
                session_id,
                payload["user_id"],
                payload["title"],
                payload["scenario"],
                payload.get("situation") or payload["scenario"],
                Jsonb(payload.get("focus_goals") or []),
                payload.get("source") or "live",
                payload.get("status") or "ready",
                Jsonb(payload["last_report"]) if payload.get("last_report") is not None else None,
            ),
        ).fetchone()
    return session_public(row)


def list_sessions(user_id: str) -> list[dict[str, Any]]:
    with get_conn() as conn:
        rows = conn.execute(
            "SELECT * FROM sessions WHERE user_id = %s ORDER BY updated_at DESC LIMIT 50",
            (user_id,),
        ).fetchall()
    return [session_public(row) for row in rows]


def save_message(session_id: str, role: str, content: str, t: float | None, metadata: dict[str, Any]) -> dict[str, Any]:
    with get_conn() as conn:
        row = conn.execute(
            """
            INSERT INTO agent_messages (id, session_id, role, content, t, metadata)
            VALUES (%s, %s, %s, %s, %s, %s)
            RETURNING *
            """,
            (new_id("msg"), session_id, role, content, t, Jsonb(metadata or {})),
        ).fetchone()
        conn.execute("UPDATE sessions SET updated_at = now() WHERE id = %s", (session_id,))
    return message_public(row)


def list_messages(session_id: str) -> list[dict[str, Any]]:
    with get_conn() as conn:
        rows = conn.execute(
            "SELECT * FROM agent_messages WHERE session_id = %s ORDER BY created_at ASC LIMIT 200",
            (session_id,),
        ).fetchall()
    return [message_public(row) for row in rows]
