#!/usr/bin/env python3
"""Checkout entry point; the installable skill owns the shared client."""

from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent / ".agents/skills/playing-sts2/scripts"))
import sts2_bridge

if __name__ == "__main__":
    raise SystemExit(sts2_bridge.main())

# Keep `import bridge` callers on the same implementation as the installed CLI.
sys.modules[__name__] = sts2_bridge
