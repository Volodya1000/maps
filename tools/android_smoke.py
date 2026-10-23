"""Install the published APK and verify real Android Activity startup/restart via adb.
Run against an already-booted emulator/device. Artifacts include logcat and screenshots.
The test checks the Activity, rather than treating compilation or a surviving service as success.
"""
import argparse
import hashlib
import json
import re
import subprocess
import time
from pathlib import Path

PACKAGE = 'by.cablerouteplanner.app'

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--apk', type=Path)
    parser.add_argument('--apk-dir', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--adb', default='adb')
    parser.add_argument('--launches', type=int, default=3)
    parser.add_argument('--settle-seconds', type=int, default=10)
    parser.add_argument('--wait-map-ready', action='store_true')
    args = parser.parse_args()
    apk = args.apk
    if apk is None:
        candidates = list(args.apk_dir.rglob('*-Signed.apk')) if args.apk_dir else []
        if len(candidates) != 1:
            parser.error('Provide --apk, or --apk-dir containing exactly one Signed.apk')
        apk = candidates[0]
    if not apk.is_file():
        parser.error('APK does not exist')
    args.output.mkdir(parents=True, exist_ok=True)
    def adb(*command, check=True, binary=False, timeout=90):
        result = subprocess.run([args.adb, *command], stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                text=not binary, timeout=timeout)
        if check and result.returncode:
            raise RuntimeError(f'adb {command}: {result.stderr}')
        return result.stdout
    report = dict(package=PACKAGE, sha256=hashlib.sha256(apk.read_bytes()).hexdigest(), launches=[], success=False)
    try:
        adb('wait-for-device')
        report['android_release'] = adb('shell', 'getprop', 'ro.build.version.release').strip()
        report['abi'] = adb('shell', 'getprop', 'ro.product.cpu.abi').strip()
        adb('install', '-r', str(apk), timeout=600)
        resolved = adb('shell', 'cmd', 'package', 'resolve-activity', '--brief',
                       '-a', 'android.intent.action.MAIN', '-c', 'android.intent.category.LAUNCHER', PACKAGE)
        component = next((line.strip() for line in resolved.splitlines() if line.strip().startswith(PACKAGE + '/')), None)
        if not component:
            raise RuntimeError('Launcher Activity was not resolved: ' + resolved)
        report['activity'] = component
        for index in range(args.launches):
            adb('shell', 'am', 'force-stop', PACKAGE)
            adb('logcat', '-c')
            start = adb('shell', 'am', 'start', '-W', '-n', component, timeout=180)
            if 'Error:' in start or 'Exception' in start:
                raise RuntimeError('Activity start failed: ' + start)
            # Keep observing after am start: AppCompat failures can occur just after it returns.
            map_ready = None
            observation_seconds = args.settle_seconds if args.wait_map_ready or index == 0 else 15
            for _ in range(observation_seconds):
                time.sleep(1)
                pid = adb('shell', 'pidof', PACKAGE, check=False).strip()
                if not pid:
                    raise RuntimeError('Application died after launching Activity')
                if args.wait_map_ready:
                    current_logs = adb('logcat', '-d', '-v', 'brief')
                    map_ready = next((line for line in current_logs.splitlines()
                                      if 'CRP_OFFLINE_MAP_READY' in line), None)
                    if map_ready:
                        time.sleep(2)  # Allow the next map frame to be presented before capture.
                        break
            if args.wait_map_ready and not map_ready:
                raise RuntimeError('Activity survived but offline map did not become ready')
            activities = adb('shell', 'dumpsys', 'activity', 'activities')
            resumed = [line.strip() for line in activities.splitlines()
                       if re.search(r'(mResumedActivity|topResumedActivity|ResumedActivity:)', line)]
            if not any(PACKAGE in line for line in resumed):
                raise RuntimeError('Application Activity is not resumed: ' + '\n'.join(resumed))
            logs = adb('logcat', '-d', '-v', 'threadtime')
            (args.output / f'launch-{index+1}.logcat.txt').write_text(logs)
            # The crash buffer avoids false positives from unrelated system services.
            crashes = adb('logcat', '-b', 'crash', '-d')
            if PACKAGE in crashes:
                raise RuntimeError('Application crash buffer is not empty: ' + crashes)
            (args.output / f'launch-{index+1}.png').write_bytes(adb('exec-out', 'screencap', '-p', binary=True))
            report['launches'].append(dict(attempt=index+1, pid=pid, resumed=resumed, map_ready=map_ready))
        report['success'] = True
    except Exception as error:
        report['error'] = str(error)
        raise
    finally:
        try:
            (args.output / 'last.logcat.txt').write_text(adb('logcat', '-d', '-v', 'threadtime', check=False))
        finally:
            (args.output / 'report.json').write_text(json.dumps(report, indent=2) + '\n')
            print(json.dumps(report), flush=True)

if __name__ == '__main__':
    main()
