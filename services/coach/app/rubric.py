"""Rubric loader — per-scenario YAML files → composed LLM prompt block.

Each rubric is a YAML file in services/coach/rubrics/. Coach picks the file by
SessionBundle.scenario (defaults to "presentation"). New scenarios = drop a YAML
file in that directory; no code changes required. This is the core enabler for
the multi-scenario coaching pivot (presentation / interview / vocal / ...).

The composed scenario block is sandwiched between the universal SYSTEM_PROMPT_BASE
(absolute non-negotiables + event glossary) and OUTPUT_INSTRUCTION (format/validation
rules) in prompts.py.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from functools import lru_cache
from pathlib import Path
from typing import Dict, List

import yaml


RUBRICS_DIR = Path(__file__).resolve().parent.parent / "rubrics"


@dataclass(frozen=True)
class AxisRubric:
    weight: float
    text: str


@dataclass(frozen=True)
class Rubric:
    id: str
    display_name: str
    description: str
    audience_perspective: str
    axes: Dict[str, AxisRubric]
    event_kind_emphasis: Dict[str, str] = field(default_factory=dict)
    top_priority_count: int = 3

    def format_axes_block(self) -> str:
        """Returns the per-axis rubric markdown ready to drop into the LLM prompt."""
        lines = ["## rubric 5축 점수 (0~5, 0.5 단위) — 시나리오별 가중·해석"]
        lines.append("")
        lines.append("채점 기준 — 3점이 평균, 5점은 트레이너가 \"데모로 써도 좋다\"고 할 정도. 점수 인플레이션 금지.")
        lines.append("")
        for axis_id, axis in self.axes.items():
            lines.append(f"### {axis_id} (시나리오 가중 {axis.weight:.2f})")
            lines.append(axis.text.rstrip())
            lines.append("")
        return "\n".join(lines)


def _load_yaml(path: Path) -> Rubric:
    with open(path, encoding="utf-8") as f:
        data = yaml.safe_load(f)
    axes_raw = data.get("axes") or {}
    axes = {
        axis_id: AxisRubric(
            weight=float(axis_data.get("weight", 1.0)),
            text=str(axis_data.get("text", "")).strip(),
        )
        for axis_id, axis_data in axes_raw.items()
    }
    return Rubric(
        id=data["id"],
        display_name=data.get("display_name", data["id"]),
        description=data.get("description", "").strip(),
        audience_perspective=data.get("audience_perspective", "").strip(),
        axes=axes,
        event_kind_emphasis=dict(data.get("event_kind_emphasis", {}) or {}),
        top_priority_count=int(data.get("top_priority_count", 3)),
    )


@lru_cache(maxsize=32)
def load_rubric(scenario: str) -> Rubric:
    """Load a scenario's rubric. Falls back to 'presentation' if the requested
    scenario YAML is missing — keeps the system from breaking on a typo."""
    path = RUBRICS_DIR / f"{scenario}.yaml"
    if not path.exists():
        path = RUBRICS_DIR / "presentation.yaml"
    return _load_yaml(path)


def list_available_scenarios() -> List[str]:
    return sorted(p.stem for p in RUBRICS_DIR.glob("*.yaml"))


def compose_scenario_block(scenario: str) -> str:
    """Returns the scenario-specific block to insert between SYSTEM_PROMPT_BASE and
    OUTPUT_INSTRUCTION. Cacheable per-scenario (via lru_cache on load_rubric)."""
    r = load_rubric(scenario)
    parts = [
        f"# 시나리오: {r.display_name} (id={r.id})",
        r.description,
        "",
        "## 청중·평가자 관점",
        r.audience_perspective,
        "",
        r.format_axes_block(),
        f"## top_priorities — 정확히 {r.top_priority_count}개",
        "가장 임팩트 큰 개선 항목을 우선순위대로. 각각:",
        "- `text`: 구체적 수치 + 시점 + 청중 인지 영향",
        "- `evidence_t`: 가장 대표적인 이벤트 구간",
        "- `suggestion`: 한 줄 처방",
        "",
    ]
    if r.event_kind_emphasis:
        parts.append("## 이벤트 심각도 (시나리오 우선순위)")
        parts.append("입력 `events`의 각 kind에 대해 이 시나리오에서는 아래 심각도로 해석:")
        for kind, level in r.event_kind_emphasis.items():
            parts.append(f"- `{kind}` → {level}")
        parts.append("")
    return "\n".join(parts)
