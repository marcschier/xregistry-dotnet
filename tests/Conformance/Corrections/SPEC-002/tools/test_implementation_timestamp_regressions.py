"""Calendar-valid timestamp examples, discovered by the independent .NET validator."""

from datetime import datetime
from pathlib import Path
import re


def test_cloudevents_created_and_modified_examples_are_calendar_valid():
    text = (Path(__file__).resolve().parents[1] / "cloudevents" / "spec.md").read_text(encoding="utf-8")
    pairs = re.findall(
        r'"createdat":\s*"(\d{4}-[^"]+)",\s*"modifiedat":\s*"(\d{4}-[^"]+)"',
        text,
    )
    assert len(pairs) >= 11
    for created, modified in pairs:
        created_time = datetime.fromisoformat(created.replace("Z", "+00:00"))
        modified_time = datetime.fromisoformat(modified.replace("Z", "+00:00"))
        assert modified_time > created_time
