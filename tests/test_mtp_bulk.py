"""Exercise the native WPD bulk callback without a phone."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


class MtpBulkTests(unittest.TestCase):
    def test_bulk_dispatch_and_fallbacks(self):
        with tempfile.TemporaryDirectory(prefix='crispy-bulk-tests-') as work:
            output = Path(work) / 'mtp-bulk-tests.exe'
            compiler = Path(os.environ['SystemRoot']) / 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
            subprocess.run([str(compiler), '/nologo', '/target:exe', '/platform:x64', '/codepage:65001',
                            f'/out:{output}', str(ROOT / 'tests/mtp-bulk.cs'), str(ROOT / 'gui/MtpDevice.cs'),
                            str(ROOT / 'gui/PortableDeviceInterop.cs')], check=True, timeout=30)
            result = subprocess.run([str(output)], capture_output=True, timeout=30)
            self.assertEqual(result.returncode, 0, (result.stdout + result.stderr).decode('utf-8', errors='replace'))
