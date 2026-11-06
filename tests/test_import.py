"""Offline integration tests. Development only: python -m unittest discover -s tests -v"""
import json
import os
from pathlib import Path
import shutil
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[1]
PS = Path(os.environ['SystemRoot']) / 'System32/WindowsPowerShell/v1.0/powershell.exe'


class ImportTests(unittest.TestCase):
    def setUp(self):
        self.work = ROOT / '.test-work' / self.id().split('.')[-1]
        if not self.work.resolve().is_relative_to(ROOT / '.test-work'):
            raise ValueError(f'Unexpected test path: {self.work}')
        if self.work.exists():
            shutil.rmtree(self.work)
        self.work.mkdir(parents=True)
        self.dest = self.work / '导出 空格'
        self.config_path = self.work / 'device.json'
        self.calls = self.work / 'transfers.txt'
        self.config = {'targets': [{'id': 'TEST-USB', 'state': 'Connected'}],
                       'albums': ['相机', '截图'], 'files': [], 'directories': [], 'mode': ''}
        self.add_file('相机', "中文 空格 O'Brien $photo.jpg", b'photo-original')
        self.add_file('相机', 'video.mp4', b'video-original-contents')
        self.add_file('截图', 'screen.png', b'screenshot')

    def add_file(self, album, name, content):
        source = self.work / ('source-' + str(len(self.config['files'])))
        source.write_bytes(content)
        self.config['files'].append({'remote': f'/mnt/data/100/media_fuse/Photo/{album}/{name}',
                                     'size': len(content), 'source': str(source)})

    def run_import(self, *args, ok=True):
        self.config_path.write_text(json.dumps(self.config, ensure_ascii=False), encoding='utf-8')
        env = {**os.environ, 'HARMONY_TEST_CONFIG': str(self.config_path),
               'HARMONY_TEST_TRANSFERS': str(self.calls)}
        result = subprocess.run([str(PS), '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                                 str(ROOT / 'Import-HarmonyPhotos.ps1'), '-Destination', str(self.dest),
                                 '-NonInteractive', '-HdcPath', str(ROOT / 'tests/fake-hdc.ps1'), *args],
                                env=env, capture_output=True, timeout=60)
        output = result.stdout.decode('utf-8', errors='replace') + result.stderr.decode('utf-8', errors='replace')
        self.assertEqual(result.returncode, 0 if ok else 1, output)
        return output

    def summary(self):
        return json.loads((self.dest / '.harmony-import/summary.json').read_text(encoding='utf-8-sig'))

    def test_copy_resume_and_repair_same_size_corruption(self):
        self.run_import()
        self.assertEqual(self.summary()['copied'], 2)
        for file in self.config['files'][:2]:
            p = self.dest / '相机' / file['remote'].rsplit('/', 1)[1]
            self.assertEqual(p.read_bytes(), Path(file['source']).read_bytes())
        self.run_import()
        self.assertEqual(self.summary()['verifiedSkipped'], 2)
        self.assertEqual(len(self.calls.read_text(encoding='utf-8').splitlines()), 2)
        p.write_bytes(b'X' * p.stat().st_size)
        self.run_import()
        self.assertEqual(self.summary()['copied'], 1)
        self.assertEqual(p.read_bytes(), Path(self.config['files'][1]['source']).read_bytes())

    def test_all_albums(self):
        self.run_import('-AllAlbums')
        self.assertEqual(self.summary()['totalFiles'], 3)
        self.assertTrue((self.dest / '截图/screen.png').exists())

    def test_nested_directory_and_quoted_album(self):
        self.config['albums'] = ["O'Brien 相机"]
        self.config['files'] = []
        self.config['directories'] = [{'parent': "/mnt/data/100/media_fuse/Photo/O'Brien 相机", 'name': '子目录'}]
        self.add_file("O'Brien 相机/子目录", 'nested.jpg', b'nested')
        self.run_import('-AllAlbums')
        self.assertEqual((self.dest / "O'Brien 相机/子目录/nested.jpg").read_bytes(), b'nested')

    def test_list_and_preview_do_not_create_destination(self):
        self.assertIn('相机', self.run_import('-ListAlbums'))
        self.assertFalse(self.dest.exists())
        self.run_import('-DryRun')
        self.assertFalse(self.dest.exists())

    def test_no_device_or_authorization(self):
        self.config['targets'] = []
        self.run_import(ok=False)
        self.config['targets'] = [{'id': 'TEST-USB', 'state': 'Unauthorized'}]
        self.run_import(ok=False)
        self.assertFalse(self.dest.exists())

    def test_multiple_devices_require_selection(self):
        self.config['targets'].append({'id': 'OTHER', 'state': 'Connected'})
        self.run_import(ok=False)
        self.run_import('-Device', 'TEST-USB')

    def test_unrecorded_target_is_not_overwritten(self):
        p = self.dest / '相机/video.mp4'
        p.parent.mkdir(parents=True)
        p.write_bytes(b'existing-important-file')
        self.run_import(ok=False)
        self.assertEqual(p.read_bytes(), b'existing-important-file')
        self.assertFalse(self.calls.exists())

    def test_errors_with_zero_exit_status_and_wrong_size(self):
        for mode in ['failzero', 'wrongsize']:
            with self.subTest(mode=mode):
                self.config['mode'] = mode
                self.run_import(ok=False)
                self.assertFalse((self.dest / '.harmony-import/summary.json').exists())
                self.assertFalse((self.dest / '相机/video.mp4').exists())

    def test_disconnect_resume(self):
        self.config['mode'] = 'midfail'
        self.run_import(ok=False)
        self.assertFalse((self.dest / '.harmony-import/summary.json').exists())
        self.config['mode'] = ''
        self.run_import()
        self.assertEqual(self.summary()['copied'], 1)
        self.assertEqual(self.summary()['verifiedSkipped'], 1)

    def test_unsupported_and_malformed_listing(self):
        for mode in ['unsupported', 'malformed']:
            self.config['mode'] = mode
            self.run_import(ok=False)
        self.assertFalse(self.dest.exists())

    def test_invalid_windows_filename_and_case_collision(self):
        self.add_file('相机', 'NUL.jpg', b'x')
        self.run_import(ok=False)
        self.config['files'].pop()
        self.add_file('相机', 'VIDEO.mp4', b'x')
        self.run_import(ok=False)
        self.assertFalse(self.dest.exists())

    def test_changed_device_and_stale_success_report(self):
        self.run_import()
        self.config['targets'][0]['id'] = 'OTHER'
        self.run_import(ok=False)
        self.assertFalse((self.dest / '.harmony-import/summary.json').exists())

    def test_incomplete_journal_and_pending_atomic_move(self):
        self.run_import()
        journal = self.dest / '.harmony-import/completed.jsonl'
        with journal.open('a', encoding='utf-8') as f:
            f.write('{incomplete')
        # Simulate loss after a validated receipt was recorded, before rename.
        (self.dest / '相机/video.mp4').unlink()
        self.run_import()
        self.assertEqual(self.summary()['copied'], 1)
        self.assertEqual(self.summary()['verifiedSkipped'], 1)
        self.run_import()
        self.assertEqual(self.summary()['verifiedSkipped'], 2)


if __name__ == '__main__':
    unittest.main()
