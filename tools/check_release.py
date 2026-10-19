"""Validate the explicit source-only publication set; never prints file contents."""
import argparse
import pathlib
import re
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
MANIFEST = "tools/public-files.txt"
IGNORED_DIRS = {".git", "build", "bin", "obj", "artifacts", "__pycache__"}
ALLOWED_SUFFIXES = {".cs", ".csproj", ".ps1", ".py", ".md", ".txt", ".html"}


def git(*args):
    return subprocess.check_output(["git", "-C", str(ROOT), *args])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--working-tree", action="store_true", help="Check source before staging")
    options = parser.parse_args()
    raw = (ROOT / MANIFEST).read_bytes() if options.working_tree else git("show", ":" + MANIFEST)
    entries = [line.strip() for line in raw.decode("utf-8").splitlines() if line.strip() and not line.startswith("#")]
    if len(entries) != len(set(entries)):
        raise ValueError("Duplicate publication entries")
    expected = set(entries)
    if options.working_tree:
        actual = {p.relative_to(ROOT).as_posix() for p in ROOT.rglob("*")
                  if p.is_file() and not (set(p.relative_to(ROOT).parts[:-1]) & IGNORED_DIRS)}
    else:
        actual = set(git("ls-files", "-z").decode("utf-8").strip("\0").split("\0"))
        for entry in git("ls-files", "--stage", "-z").decode("utf-8").strip("\0").split("\0"):
            if not entry.startswith("100644 ") and not entry.startswith("100755 "):
                raise ValueError("Non-regular staged file rejected")
    if actual != expected:
        raise ValueError("Publication set mismatch: missing=" + str(sorted(expected - actual)) + "; unlisted=" + str(sorted(actual - expected)))
    total = 0
    for name in sorted(expected):
        path = pathlib.PurePosixPath(name)
        if path.is_absolute() or ".." in path.parts or "\\" in name or ":" in name:
            raise ValueError("Unsafe manifest path")
        if path.suffix not in ALLOWED_SUFFIXES and name not in {".gitattributes", ".gitignore", "LICENSE", "examples/synthetic-gruz.json"}:
            raise ValueError("Disallowed file type: " + name)
        local = ROOT / name
        if local.is_symlink() or any(p.is_symlink() for p in local.parents if p != ROOT.parent):
            raise ValueError("Symlink rejected: " + name)
        data = local.read_bytes() if options.working_tree else git("show", ":" + name)
        if len(data) > 400_000 or b"\x00" in data:
            raise ValueError("Oversized or binary file rejected: " + name)
        content = data.decode("utf-8-sig")
        if re.search(r"[A-Z]:[\\/]+Users[\\/]+[^\s\\/]+|/(?:home|Users)/[^\s/]+", content, re.I):
            raise ValueError("Absolute home path requires review: " + name)
        total += len(data)
    print(f"PASS: {len(expected)} explicitly allowed UTF-8 source files, {total} bytes; no binary/generated additions. Run independent secret and history scans too.")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, subprocess.CalledProcessError, UnicodeError) as error:
        print("FAIL:", type(error).__name__, str(error), file=sys.stderr)
        sys.exit(1)
