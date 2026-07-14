#!/usr/bin/env python3
"""Validate the fixed structure and safety rules of the progressive F# tutorial."""

from __future__ import annotations

import hashlib
import json
import re
import sys
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
TUTORIAL_ROOT = ROOT / "tutorials" / "fsharp"
DOCS_ROOT = ROOT / "docs" / "tutorial"

CHAPTERS = (
    ("01-first-agent", "FirstAgent.fsproj", "Program.fs"),
    ("02-building-blocks", "BuildingBlocks.fsproj", "Program.fs"),
    ("03-validation", "Validation.fsproj", "Program.fs"),
    ("04-failures", "Failures.fsproj", "Program.fs"),
    ("05-structured-output", "StructuredOutput.fsproj", "Program.fs"),
    ("06-streaming", "Streaming.fsproj", "Program.fs"),
    ("07-sessions", "Sessions.fsproj", "Program.fs"),
    ("08-tools", "Tools.fsproj", "Program.fs"),
    ("09-approvals", "Approvals.fsproj", "Program.fs"),
    ("10-skills", "Skills.fsproj", "Program.fs"),
    ("11-circuit-programs", "CircuitComposition.fsproj", "Program.fs"),
    ("12-parallel-programs", "ParallelPrograms.fsproj", "Program.fs"),
    ("13-workflows", "Pipelines.fsproj", "Program.fs"),
    ("14-human-review", "HumanReview.fsproj", "Program.fs"),
    ("15-checkpoints", "Checkpoints.fsproj", "Program.fs"),
    ("16-testing", "Testing.fsproj", "Tests.fs"),
    ("17-telemetry", "Telemetry.fsproj", "Program.fs"),
    ("18-switch-providers", "SwitchProviders.fsproj", "Program.fs"),
)

HEADINGS = (
    "What you will build",
    "The idea",
    "Create or open the project",
    "Complete source",
    "Run it",
    "What changed",
    "Check your understanding",
    "Try it yourself",
    "Recap and next step",
)


def text_of(root: ET.Element, name: str) -> str | None:
    node = root.find(f".//{name}")
    return node.text.strip() if node is not None and node.text else None


def toc_entries(path: Path) -> list[tuple[str, str]]:
    """Read this repository's deliberately simple name/href Docfx TOC shape."""
    entries: list[tuple[str, str]] = []
    current_name: str | None = None
    for line in path.read_text(encoding="utf-8").splitlines():
        name_match = re.fullmatch(r"\s*- name:\s*(.+?)\s*", line)
        if name_match:
            current_name = name_match.group(1)
            continue

        href_match = re.fullmatch(r"\s+href:\s*(.+?)\s*", line)
        if href_match and current_name is not None:
            entries.append((current_name, href_match.group(1)))
            current_name = None
    return entries


def main() -> int:
    errors: list[str] = []
    expected_slugs = {chapter[0] for chapter in CHAPTERS}
    actual_dirs = {path.name for path in TUTORIAL_ROOT.iterdir() if path.is_dir()} if TUTORIAL_ROOT.exists() else set()
    if actual_dirs != expected_slugs:
        errors.append(f"chapter directories differ: expected {sorted(expected_slugs)}, found {sorted(actual_dirs)}")

    all_projects = list(TUTORIAL_ROOT.rglob("*.fsproj")) if TUTORIAL_ROOT.exists() else []
    if len(all_projects) != len(CHAPTERS):
        errors.append(f"expected 18 tutorial projects, found {len(all_projects)}")

    expected_project_paths = {f"tutorials/fsharp/{slug}/{project}" for slug, project, _ in CHAPTERS}
    try:
        solution_root = ET.parse(ROOT / "CircuitDotNet.slnx").getroot()
    except (ET.ParseError, OSError) as error:
        errors.append(f"could not parse CircuitDotNet.slnx: {error}")
    else:
        solution_project_paths = {
            node.get("Path", "").replace("\\", "/")
            for node in solution_root.findall(".//Project")
            if node.get("Path", "").replace("\\", "/").startswith("tutorials/fsharp/")
        }
        if solution_project_paths != expected_project_paths:
            errors.append(
                "solution tutorial projects differ: "
                f"expected {sorted(expected_project_paths)}, found {sorted(solution_project_paths)}"
            )

    root_toc_path = ROOT / "toc.yml"
    if not root_toc_path.is_file() or ("Tutorial", "docs/tutorial/toc.yml") not in toc_entries(root_toc_path):
        errors.append("root toc.yml must link Tutorial to docs/tutorial/toc.yml")

    tutorial_toc_path = DOCS_ROOT / "toc.yml"
    expected_tutorial_hrefs = ["index.md", *(f"{slug}.md" for slug, _, _ in CHAPTERS)]
    if not tutorial_toc_path.is_file():
        errors.append("missing docs/tutorial/toc.yml")
    else:
        actual_tutorial_hrefs = [href for _, href in toc_entries(tutorial_toc_path)]
        if actual_tutorial_hrefs != expected_tutorial_hrefs:
            errors.append(
                "tutorial toc pages differ or are out of order: "
                f"expected {expected_tutorial_hrefs}, found {actual_tutorial_hrefs}"
            )

    try:
        docfx = json.loads((ROOT / "docfx.json").read_text(encoding="utf-8"))
        resource_patterns = {
            pattern
            for resource in docfx["build"]["resource"]
            for pattern in resource.get("files", [])
        }
        if "tutorials/fsharp/**/*.fs" not in resource_patterns:
            errors.append("docfx resources must include tutorials/fsharp/**/*.fs")
    except (KeyError, OSError, json.JSONDecodeError, TypeError) as error:
        errors.append(f"could not validate docfx.json resources: {error}")

    expected_pages = {DOCS_ROOT / f"{slug}.md" for slug, _, _ in CHAPTERS}
    actual_pages = set(DOCS_ROOT.glob("[0-9][0-9]-*.md")) if DOCS_ROOT.exists() else set()
    if actual_pages != expected_pages:
        errors.append(
            "numbered chapter pages differ: "
            f"expected {[path.name for path in sorted(expected_pages)]}, "
            f"found {[path.name for path in sorted(actual_pages)]}"
        )

    for slug, project_name, source_name in CHAPTERS:
        chapter_dir = TUTORIAL_ROOT / slug
        project = chapter_dir / project_name
        source = chapter_dir / source_name
        page = DOCS_ROOT / f"{slug}.md"
        lock_file = chapter_dir / "packages.lock.json"
        for required in (project, source, page, lock_file):
            if not required.is_file():
                errors.append(f"missing {required.relative_to(ROOT)}")

        if project.is_file():
            try:
                project_xml = ET.parse(project).getroot()
            except ET.ParseError as error:
                errors.append(f"invalid XML in {project.relative_to(ROOT)}: {error}")
            else:
                if text_of(project_xml, "TargetFramework") != "net10.0":
                    errors.append(f"{project.relative_to(ROOT)} must target net10.0")
                if text_of(project_xml, "IsPackable") != "false":
                    errors.append(f"{project.relative_to(ROOT)} must set IsPackable=false")
                compile_files = [node.get("Include") for node in project_xml.findall(".//Compile")]
                if compile_files != [source_name]:
                    errors.append(f"{project.relative_to(ROOT)} must compile only {source_name}")
                if any("tutorial" in (node.get("Include") or "").lower() for node in project_xml.findall(".//ProjectReference")):
                    errors.append(f"{project.relative_to(ROOT)} references a shared tutorial project")

                output_type = text_of(project_xml, "OutputType")
                is_test = text_of(project_xml, "IsTestProject")
                if source_name == "Tests.fs":
                    if is_test != "true" or output_type == "Exe":
                        errors.append(f"{project.relative_to(ROOT)} must be an xUnit test project")
                elif output_type != "Exe" or is_test == "true":
                    errors.append(f"{project.relative_to(ROOT)} must be an executable")

        if page.is_file():
            page_text = page.read_text(encoding="utf-8")
            expected_include = f"[!code-fsharp](../../tutorials/fsharp/{slug}/{source_name})"
            includes = re.findall(r"\[!code-fsharp\]\([^)]+\)", page_text)
            if includes != [expected_include]:
                errors.append(f"{page.relative_to(ROOT)} must contain exactly its matching complete-source include")
            page_headings = tuple(re.findall(r"^## (.+?)\s*$", page_text, re.MULTILINE))
            if page_headings != HEADINGS:
                errors.append(f"{page.relative_to(ROOT)} does not have the nine approved sections in order")

    allowed_files = {
        TUTORIAL_ROOT / slug / file
        for slug, project, source in CHAPTERS
        for file in (project, source, "packages.lock.json")
    }
    tutorial_files = [
        path
        for path in TUTORIAL_ROOT.rglob("*")
        if path.is_file() and not ({"bin", "obj"} & set(path.relative_to(TUTORIAL_ROOT).parts))
    ]
    extra_source = [path for path in tutorial_files if path.suffix == ".fs" and path not in allowed_files]
    if extra_source:
        errors.append("shared or unexpected tutorial source: " + ", ".join(str(path.relative_to(ROOT)) for path in extra_source))

    source_hashes: dict[str, list[str]] = {}
    for slug, _, source_name in CHAPTERS:
        source = TUTORIAL_ROOT / slug / source_name
        if source.is_file():
            digest = hashlib.sha256(source.read_bytes()).hexdigest()
            source_hashes.setdefault(digest, []).append(slug)
    duplicates = [slugs for slugs in source_hashes.values() if len(slugs) > 1]
    if duplicates:
        errors.append("tutorial chapters must progress rather than duplicate source: " + repr(duplicates))

    capability_markers = {
        "11-circuit-programs": ("Circuit.thenStep", "Circuit.run"),
        "12-parallel-programs": ("Circuit.keyedItems", "WithMaxConcurrency", "completion-handoff"),
        "13-workflows": ("Circuit.thenDynamic", "Circuit.collectSourceOrder", "Circuit.stream", "Circuit.start"),
        "14-human-review": ("Circuit.approval", "RespondAsync", "single-use"),
        "15-checkpoints": ("CircuitCheckpoint", ".Serialize()", "Circuit.resume", '"create"', '"resume"'),
        "16-testing": ("ScriptedRuntime", "ScriptedResponses.ForNode", "Circuit.thenDynamic"),
    }
    for slug, markers in capability_markers.items():
        source_name = next(source for chapter, _, source in CHAPTERS if chapter == slug)
        content = (TUTORIAL_ROOT / slug / source_name).read_text(encoding="utf-8")
        missing = [marker for marker in markers if marker not in content]
        if missing:
            errors.append(f"{slug} is missing required progressive capability markers: {missing}")

    scan_files = list(DOCS_ROOT.rglob("*.md")) + [ROOT / "docs" / "getting-started" / "fsharp.md"] + [path for path in tutorial_files if path.suffix == ".fs"]
    package_command = re.compile(
        r"^\s*(?:\$\s*)?dotnet\s+add\s+package\s+CircuitDotNet(?:\.|\b)",
        re.IGNORECASE | re.MULTILINE,
    )
    openai_credential = re.compile(r"\bsk-(?:proj-)?[A-Za-z0-9_-]{16,}\b", re.IGNORECASE)
    credential_assignment = re.compile(
        r"(?i)\b(?:[a-z][a-z0-9_]*_)?(?:api[_-]?key|token|secret|password)\b"
        r"\s*(?:=|:)\s*[\"']([^\"']+)[\"']"
    )
    harmless = re.compile(r"^(?:your[-_ ]|<|example|placeholder|redacted|not-set)", re.IGNORECASE)
    stale_api = re.compile(r"\bAgent\.(?:run|start)\b|\bCircuit\.call\b|\bcircuit\s*\{|\bI(?:InteractiveCircuitRuntime|WorkflowRuntime)\b|\bWorkflow(?:Definition|Checkpoint|RunOptions)\b|\bWorkflow\.")
    for path in scan_files:
        content = path.read_text(encoding="utf-8")
        if stale_api.search(content):
            errors.append(f"{path.relative_to(ROOT)} teaches a removed execution API")
        if package_command.search(content):
            errors.append(f"{path.relative_to(ROOT)} presents an unpublished CircuitDotNet package command")
        for match in openai_credential.finditer(content):
            credential = match.group(0).lower()
            if not any(marker in credential for marker in ("your", "example", "placeholder", "redacted")):
                errors.append(f"{path.relative_to(ROOT)} contains an OpenAI credential-shaped value")
        for match in credential_assignment.finditer(content):
            value = match.group(1).strip()
            if value and not harmless.match(value):
                errors.append(f"{path.relative_to(ROOT)} contains an obvious literal credential assignment")

    if errors:
        print("F# tutorial validation failed:", file=sys.stderr)
        for error in errors:
            print(f"- {error}", file=sys.stderr)
        return 1

    print("F# tutorial validation passed: 18 projects, 18 pages, and 18 matching source includes.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
