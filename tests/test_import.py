"""Offline integration tests. Development only: python -m unittest discover -s tests -v"""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
PS = Path(os.environ['SystemRoot']) / 'System32/WindowsPowerShell/v1.0/powershell.exe'


class ImportTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        work = tempfile.TemporaryDirectory(prefix='crispy-photo-assembly-')
        cls.addClassCleanup(work.cleanup)
        cls.mtp = Path(work.name) / 'fake-mtp.dll'
        compiler = Path(os.environ['SystemRoot']) / 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
        subprocess.run([str(compiler), '/nologo', '/target:library', '/platform:x64', '/codepage:65001',
                        f'/out:{cls.mtp}', str(ROOT / 'tests/fake-mtp.cs')], check=True, timeout=30)

    def setUp(self):
        work = tempfile.TemporaryDirectory(prefix='crispy-photo-import-')
        self.addCleanup(work.cleanup)
        self.work = Path(work.name)
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

    def run_import(self, *args, ok=True, mtp_mode='', mtp_session='first'):
        self.config_path.write_text(json.dumps(self.config, ensure_ascii=False), encoding='utf-8')
        env = {**os.environ, 'HARMONY_TEST_CONFIG': str(self.config_path),
               'HARMONY_TEST_TRANSFERS': str(self.calls), 'MTP_TEST_MODE': mtp_mode,
               'MTP_TEST_SESSION': mtp_session}
        result = subprocess.run([str(PS), '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                                 str(ROOT / 'Import-HarmonyPhotos.ps1'), '-Destination', str(self.dest),
                                 '-NonInteractive', '-HdcPath', str(ROOT / 'tests/fake-hdc.ps1'), *args],
                                env=env, capture_output=True, timeout=60)
        output = result.stdout.decode('utf-8', errors='replace') + result.stderr.decode('utf-8', errors='replace')
        self.assertEqual(result.returncode, 0 if ok else 1, output)
        return output

    def summary(self):
        return json.loads((self.dest / '.harmony-import/summary.json').read_text(encoding='utf-8-sig'))

    def run_gui(self, data, *args, **kwargs):
        request = self.work / 'request.json'
        request.write_text(json.dumps(data), encoding='utf-8')
        output = self.run_import(*args, '-GuiRequest', str(request), **kwargs)
        return [json.loads(line.removeprefix('HARMONY_GUI ')) for line in output.splitlines()
                if line.startswith('HARMONY_GUI ')]

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

    def test_album_size_estimates_selected_and_all_without_copying(self):
        self.config['albums'].append('空相册')
        self.config['directories'] = [{'parent': '/mnt/data/100/media_fuse/Photo/相机', 'name': '子目录'}]
        self.add_file('相机/子目录', 'nested.jpg', b'nested')
        self.config['files'][2]['size'] = 2 * 1024 ** 4
        camera_bytes = sum(f['size'] for f in self.config['files'] if '/相机/' in f['remote'])
        request = dict(action='estimate', device='TEST-USB', deviceKind='Hdc',
                       destination=str(self.dest), album='相机', allAlbums=False)
        for album, all_albums, expected in [('相机', False, camera_bytes), ('空相册', False, 0),
                                            ('', True, camera_bytes + 2 * 1024 ** 4)]:
            with self.subTest(album=album, all_albums=all_albums):
                request.update(album=album, allAlbums=all_albums)
                events = self.run_gui(request)
                self.assertEqual([e['totalBytes'] for e in events if e['kind'] == 'albumSize'], [expected])
        request.update(deviceKind='Mtp', allAlbums=True)
        events = self.run_gui(request, '-MtpAssembly', str(self.mtp))
        self.assertEqual([e['totalBytes'] for e in events if e['kind'] == 'albumSize'], [12])
        self.assertFalse(self.dest.exists())
        self.assertFalse(self.calls.exists())

    def test_progress_counts_verified_bytes_after_each_file_and_on_resume(self):
        first, second = (file['size'] for file in self.config['files'][:2])
        total = first + second
        request = dict(action='import', device='TEST-USB', deviceKind='Hdc',
                       destination=str(self.dest), album='相机', allAlbums=False)

        def progress(**kwargs):
            return [(e['doneBytes'], e['totalBytes'], e['done'], e['total']) for e in self.run_gui(request, **kwargs)
                    if e['kind'] == 'progress']

        self.config['mode'] = 'midfail'
        self.assertEqual(progress(ok=False), [(0, total, 0, 2), (first, total, 1, 2)])
        self.config['mode'] = ''
        self.assertEqual(progress(), [(first, total, 1, 2), (total, total, 2, 2)])
        self.assertEqual(progress(), [(total, total, 2, 2)])

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

    def test_mtp_auto_detect_and_resume_with_changed_object_ids(self):
        self.config['targets'] = []
        args = ('-MtpAssembly', str(self.mtp))
        self.run_import(*args, mtp_mode='disconnect', ok=False)
        self.assertFalse((self.dest / '.harmony-import/summary.json').exists())
        self.assertEqual((self.dest / "内部存储/DCIM/Camera/照片 O'Brien.jpg").read_bytes(), b'jpeg')
        self.run_import(*args, mtp_mode='short', mtp_session='second', ok=False)
        self.assertFalse((self.dest / '内部存储/DCIM/Camera/视频.mp4').exists())
        self.run_import(*args, mtp_session='third')
        self.assertEqual(self.summary()['verifiedSkipped'], 1)
        self.assertEqual(self.summary()['copied'], 1)
        self.assertEqual((self.dest / '内部存储/DCIM/Camera/视频.mp4').read_bytes(), b'mp4!')
        self.assertFalse((self.dest / 'SD 卡').exists())
        self.run_import(*args, '-AllAlbums', mtp_mode='undated')
        self.assertEqual(self.summary()['verifiedSkipped'], 1)
        self.assertEqual(self.summary()['copied'], 2)
        self.assertEqual((self.dest / 'SD 卡/Pictures/截图.png').read_bytes(), b'png!')

    def test_mixed_device_gui_selection_and_missing_phone(self):
        data = dict(action='browse', device='', deviceKind='', destination=str(self.dest), album='', allAlbums=True)

        def run_gui(**kwargs):
            return self.run_gui(data, '-MtpAssembly', str(self.mtp), **kwargs)

        devices, _ = run_gui(ok=False)
        self.assertEqual({item['Kind'] for item in devices['items']}, {'Hdc', 'Mtp'})
        self.assertIsNone(devices['selected'])
        data.update(device='TEST-USB', deviceKind='Mtp')
        devices, albums = run_gui()
        self.assertEqual(devices['selected']['Kind'], 'Mtp')
        self.assertEqual(albums['defaultAlbum'], 'DCIM（内部存储）')
        data['action'] = 'import'
        run_gui(mtp_mode='absent', ok=False)
        self.assertFalse(self.dest.exists())
        self.assertFalse(self.calls.exists())
        run_gui()
        self.assertEqual(self.summary()['totalFiles'], 3)
        data['deviceKind'] = 'Hdc'
        run_gui(ok=False)
        self.assertFalse(self.calls.exists())
        data['action'] = 'browse'
        data['deviceKind'] = 'Mtp'
        devices, albums = run_gui(mtp_mode='absent')
        self.assertEqual(devices['selected']['Kind'], 'Hdc')
        self.assertEqual(albums['defaultAlbum'], '相机')


if __name__ == '__main__':
    unittest.main()
