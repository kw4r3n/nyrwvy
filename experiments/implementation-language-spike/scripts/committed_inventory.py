"""Canonical committed inventories shared by preflight and workspace export."""

from collections.abc import Callable, Iterable
from dataclasses import dataclass
import hashlib
import os
from pathlib import Path, PurePosixPath
import subprocess


_INVENTORY_HEADER = b"nayamnyam-spike-committed-inventory-v1\0"
_EXCLUDED_PATH_PARTS = frozenset(
    {".git", "bin", "obj", "__pycache__", "evaluator", "results"}
)
_EXCLUDED_SUFFIXES = (".pyc",)

BlobReader = Callable[[str], bytes]


class CommittedInventoryError(ValueError):
    """Raised when committed experiment inputs cannot be exported safely."""


@dataclass(frozen=True, slots=True)
class TreeEntry:
    mode: str
    object_type: str
    object_id: str
    path: PurePosixPath


@dataclass(frozen=True, slots=True)
class InventoryEntry:
    mode: str
    path: PurePosixPath
    contents: bytes


@dataclass(frozen=True, slots=True)
class CommittedSource:
    repository: Path
    commit: str
    baseline_trees: dict[str, str]
    public_tree: str


def inspect_committed_source(
    source: Path,
    languages: tuple[str, ...],
) -> CommittedSource:
    """Resolve one commit and validate all selected source trees against it."""
    source = source.resolve()
    if not source.is_dir():
        raise CommittedInventoryError(
            f"source experiment tree is missing: {source}"
        )
    repository = Path(_git_text(source, "rev-parse", "--show-toplevel")).resolve()
    try:
        source_relative = source.relative_to(repository)
    except ValueError as error:
        raise CommittedInventoryError(
            f"source experiment tree is outside its Git repository: {source}"
        ) from error

    commit = _git_text(
        repository,
        "rev-parse",
        "--verify",
        "HEAD^{commit}",
    )
    public_relative = source_relative / "shared" / "public"
    public_tree = public_relative.as_posix()
    if _git_object_type(repository, f"{commit}:{public_tree}") != "tree":
        raise CommittedInventoryError(
            f"shared public bundle is missing: {source / 'shared' / 'public'}"
        )

    baseline_trees: dict[str, str] = {}
    dirty_paths = [public_tree]
    for language in languages:
        baseline_relative = source_relative / language
        baseline_tree = baseline_relative.as_posix()
        if _git_object_type(repository, f"{commit}:{baseline_tree}") != "tree":
            raise CommittedInventoryError(
                f"selected baseline is missing: {source / language}"
            )
        baseline_trees[language] = f"{commit}:{baseline_tree}"
        dirty_paths.append(baseline_tree)

    dirty = _git_text(
        repository,
        "status",
        "--porcelain=v1",
        "--untracked-files=no",
        "--",
        *dirty_paths,
    )
    if dirty:
        raise CommittedInventoryError(
            f"tracked source inputs are dirty:\n{dirty}"
        )
    return CommittedSource(
        repository=repository,
        commit=commit,
        baseline_trees=baseline_trees,
        public_tree=f"{commit}:{public_tree}",
    )


def capture_selected_inventories(
    source: Path,
    languages: tuple[str, ...],
) -> tuple[str, dict[str, tuple[InventoryEntry, ...]]]:
    """Capture each selected baseline plus the public tree from one commit."""
    committed_source = inspect_committed_source(source, languages)
    public_entries = capture_tree_inventory(
        _git_ls_tree(committed_source.repository, committed_source.public_tree),
        prefix=PurePosixPath("shared/public"),
        blob_reader=lambda object_id: _git_cat_blob(
            committed_source.repository,
            object_id,
        ),
    )
    inventories: dict[str, tuple[InventoryEntry, ...]] = {}
    for language in languages:
        baseline_entries = capture_tree_inventory(
            _git_ls_tree(
                committed_source.repository,
                committed_source.baseline_trees[language],
            ),
            prefix=None,
            blob_reader=lambda object_id: _git_cat_blob(
                committed_source.repository,
                object_id,
            ),
        )
        inventories[language] = combine_inventory_entries(
            baseline_entries,
            public_entries,
        )
    return committed_source.commit, inventories


def capture_tree_inventory(
    tree_bytes: bytes,
    *,
    prefix: PurePosixPath | None,
    blob_reader: BlobReader,
) -> tuple[InventoryEntry, ...]:
    """Convert one committed tree listing into its exact export entries."""
    captured: list[InventoryEntry] = []
    for entry in parse_tree_entries(tree_bytes):
        output_path = (
            prefix / entry.path
            if prefix is not None
            else entry.path
        )
        if is_excluded_path(output_path):
            continue
        captured.append(
            InventoryEntry(
                mode=entry.mode,
                path=output_path,
                contents=blob_reader(entry.object_id),
            )
        )
    return tuple(sorted(captured, key=lambda entry: entry.path.as_posix()))


def parse_tree_entries(tree_bytes: bytes) -> tuple[TreeEntry, ...]:
    """Parse and validate NUL-delimited ``git ls-tree`` output."""
    entries: list[TreeEntry] = []
    for record in tree_bytes.split(b"\0"):
        if not record:
            continue
        try:
            metadata, raw_path = record.split(b"\t", 1)
            raw_mode, raw_object_type, raw_object_id = metadata.split(b" ")
            mode = raw_mode.decode("ascii")
            object_type = raw_object_type.decode("ascii")
            object_id = raw_object_id.decode("ascii")
        except (UnicodeDecodeError, ValueError) as error:
            raise CommittedInventoryError(
                "committed tree contains malformed ls-tree output"
            ) from error
        path = safe_committed_path(os.fsdecode(raw_path))
        if mode not in {"100644", "100755"} or object_type != "blob":
            raise CommittedInventoryError(
                "committed tree contains unsafe mode "
                f"{mode} and type {object_type} for {path.as_posix()!r}"
            )
        if (
            len(object_id) not in {40, 64}
            or any(
                character not in "0123456789abcdef"
                for character in object_id
            )
        ):
            raise CommittedInventoryError(
                "committed tree contains malformed object ID for "
                f"{path.as_posix()!r}"
            )
        entries.append(
            TreeEntry(
                mode=mode,
                object_type=object_type,
                object_id=object_id,
                path=path,
            )
        )
    return tuple(entries)


def safe_committed_path(name: str) -> PurePosixPath:
    """Reject paths that cannot be exported beneath the workspace root."""
    parts = name.split("/")
    if (
        not name
        or name.startswith("/")
        or "\\" in name
        or any(part in {"", ".", ".."} for part in parts)
    ):
        raise CommittedInventoryError(
            f"committed tree contains unsafe path: {name!r}"
        )
    try:
        name.encode("utf-8")
    except UnicodeEncodeError as error:
        raise CommittedInventoryError(
            f"committed tree path is not UTF-8: {name!r}"
        ) from error
    return PurePosixPath(*parts)


def is_excluded_path(path: PurePosixPath) -> bool:
    """Return whether a committed path is excluded from trial workspaces."""
    return (
        any(part in _EXCLUDED_PATH_PARTS for part in path.parts)
        or path.name.endswith(_EXCLUDED_SUFFIXES)
    )


def combine_inventory_entries(
    *entry_groups: Iterable[InventoryEntry],
) -> tuple[InventoryEntry, ...]:
    """Combine export groups and reject destination path collisions."""
    by_path: dict[PurePosixPath, InventoryEntry] = {}
    for entries in entry_groups:
        for entry in entries:
            if entry.path in by_path:
                raise CommittedInventoryError(
                    "committed tree path collides during export: "
                    f"{entry.path.as_posix()!r}"
                )
            by_path[entry.path] = entry
    return tuple(
        by_path[path]
        for path in sorted(by_path, key=PurePosixPath.as_posix)
    )


def digest_inventory(entries: tuple[InventoryEntry, ...]) -> str:
    """Hash paths, Git modes, and exact blob bytes in canonical order."""
    digest = hashlib.sha256()
    digest.update(_INVENTORY_HEADER)
    for entry in entries:
        fields = (
            entry.mode.encode("ascii"),
            entry.path.as_posix().encode("utf-8"),
            entry.contents,
        )
        for field in fields:
            digest.update(len(field).to_bytes(8, "big"))
            digest.update(field)
    return digest.hexdigest()


def _git_object_type(repository: Path, object_name: str) -> str | None:
    completed = _run_git(
        repository,
        "cat-file",
        "-t",
        object_name,
        check=False,
        text=True,
    )
    if completed.returncode != 0:
        return None
    assert isinstance(completed.stdout, str)
    return completed.stdout.strip()


def _git_ls_tree(repository: Path, tree: str) -> bytes:
    completed = _run_git(
        repository,
        "ls-tree",
        "-r",
        "-z",
        "--full-tree",
        tree,
        text=False,
    )
    assert isinstance(completed.stdout, bytes)
    return completed.stdout


def _git_cat_blob(repository: Path, object_id: str) -> bytes:
    completed = _run_git(
        repository,
        "cat-file",
        "blob",
        object_id,
        text=False,
    )
    assert isinstance(completed.stdout, bytes)
    return completed.stdout


def _git_text(repository: Path, *arguments: str) -> str:
    completed = _run_git(repository, *arguments, text=True)
    assert isinstance(completed.stdout, str)
    return completed.stdout.strip()


def _run_git(
    repository: Path,
    *arguments: str,
    check: bool = True,
    text: bool,
) -> subprocess.CompletedProcess[str] | subprocess.CompletedProcess[bytes]:
    command = ("git", *arguments)
    try:
        completed = subprocess.run(
            command,
            cwd=repository,
            check=check,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=text,
            env=_git_environment(),
        )
    except FileNotFoundError as error:
        raise CommittedInventoryError(
            "git executable was not found"
        ) from error
    except subprocess.CalledProcessError as error:
        stderr = (
            error.stderr.decode("utf-8", errors="replace")
            if isinstance(error.stderr, bytes)
            else error.stderr
        )
        stdout = (
            error.stdout.decode("utf-8", errors="replace")
            if isinstance(error.stdout, bytes)
            else error.stdout
        )
        detail = (
            (stderr or "").strip()
            or (stdout or "").strip()
            or f"exit code {error.returncode}"
        )
        raise CommittedInventoryError(
            f"git command failed ({' '.join(command)}): {detail}"
        ) from error
    return completed


def _git_environment() -> dict[str, str]:
    environment = {
        key: value
        for key, value in os.environ.items()
        if not key.startswith("GIT_")
    }
    environment.update(
        {
            "GIT_ATTR_NOSYSTEM": "1",
            "GIT_CONFIG_COUNT": "0",
            "GIT_CONFIG_GLOBAL": os.devnull,
            "GIT_CONFIG_NOSYSTEM": "1",
            "GIT_CONFIG_SYSTEM": os.devnull,
            "LC_ALL": "C",
        }
    )
    return environment
