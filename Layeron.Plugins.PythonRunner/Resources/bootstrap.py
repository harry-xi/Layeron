"""The session bootstrap.

Runs inside the interpreter immediately after the runtime starts, before any user code. Its one job is to
make the interpreter's output reachable: pythonnet installs its own stdout wrapper during init that forwards
to whatever ``System.Console.Out`` was at that moment, and only falls back to the current one while
``sys.stdout`` is still null. A GUI process has no valid stdout handle at all, so ``print`` writes into
nothing - and a ``Console.SetOut`` afterwards is invisible to Python, because ``sys.stdout`` is no longer
null. Replacing the object from inside Python is the only fix that covers pip, numpy and user code alike.

It also installs the interactive interpreter's conveniences: the display hook that echoes an expression's
value, and the two prompts.
"""

import io
import sys

import _layeron_clr as _clr


class _LayeronSink(io.TextIOBase):
    """A text stream whose writes cross into the host.

    Line-buffered rather than pass-through: the host renders and logs whole lines, and a stream of
    half-lines would make both guess where a message ends.
    """

    def __init__(self, write):
        self._write = write
        self._buffer = ""

    @property
    def encoding(self):
        return "utf-8"

    def writable(self):
        return True

    def isatty(self):
        return False

    def write(self, text):
        if not isinstance(text, str):
            text = str(text)

        self._buffer += text
        while "\n" in self._buffer:
            line, self._buffer = self._buffer.split("\n", 1)
            self._write(line)

        return len(text)

    def flush(self):
        if self._buffer:
            self._write(self._buffer)
            self._buffer = ""


def _write(line):
    _clr._write(line)


def _display(value):
    if value is not None:
        _write(repr(value))


sys.stdout = _LayeronSink(_write)
sys.stderr = sys.stdout
sys.displayhook = _display
sys.ps1 = ">>> "
sys.ps2 = "... "
