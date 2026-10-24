"""Explicit, additive Linux deployment of an already authorized GitLab display connection."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import zipfile

ROOT=Path('/opt/fanasa-work-gitlab')
FILES={'mirror.py','test_mirror.py','fanasa-work-gitlab.service','fanasa-work-gitlab.timer','README.md','config.example.json','install.py'}

def run(args):
    p=subprocess.run(args,capture_output=True,text=True,timeout=300)
    if p.returncode:raise RuntimeError(Path(args[0]).name+' failed, exit '+str(p.returncode))
    return p.stdout

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--archive',required=True)
    parser.add_argument('--sha256',required=True)
    parser.add_argument('--apply',action='store_true')
    args=parser.parse_args()
    archive=Path(args.archive)
    digest=hashlib.sha256(archive.read_bytes()).hexdigest()
    if digest!=args.sha256.lower():raise RuntimeError('Artifact fingerprint differs')
    if not args.apply:
        print(json.dumps({'preview':True,'release':digest[:16],'target':str(ROOT)}));return
    if os.geteuid()!=0:raise RuntimeError('Installer requires administrator; runtime does not')
    os.umask(0o077)
    config=json.loads((ROOT/'config.json').read_text())
    for name in ('source','gitlab','mapping-key'):
        path=ROOT/'credentials'/name
        if not path.is_file() or path.stat().st_mode&0o077:raise RuntimeError('Restricted credential missing: '+name)
    dest=ROOT/'releases'/digest[:16]
    dest.mkdir(parents=True,exist_ok=True,mode=0o755)
    with zipfile.ZipFile(archive) as z:
        members=z.infolist()
        if {i.filename for i in members}!=FILES:raise RuntimeError('Unexpected artifact members')
        for entry in members:
            if entry.file_size>300_000 or entry.filename not in FILES:raise RuntimeError('Artifact exceeds bound')
            target=dest/entry.filename
            content=z.read(entry)
            if target.exists() and target.read_bytes()!=content:raise RuntimeError('Release path content differs')
            target.write_bytes(content);target.chmod(0o644)
    tests=subprocess.run([sys.executable,'-m','unittest','discover','-s',str(dest),'-p','test_*.py'],capture_output=True,text=True,timeout=60)
    if tests.returncode:raise RuntimeError('Artifact tests failed; not activated')
    if subprocess.run(['id','-u','fanasa-work-gitlab'],capture_output=True).returncode:
        run(['useradd','--system','--home-dir','/var/lib/fanasa-work-gitlab','--shell','/usr/sbin/nologin','fanasa-work-gitlab'])
    ROOT.chmod(0o755)
    (ROOT/'releases').chmod(0o755);dest.chmod(0o755)
    ca=Path('/etc/fanasa-work-gitlab-ca.crt')
    shutil.copyfile(config['ca_file'],ca);ca.chmod(0o644)
    config['ca_file']=str(ca)
    conf=Path('/etc/fanasa-work-gitlab.json')
    conf.write_text(json.dumps(config,ensure_ascii=False));conf.chmod(0o644)
    for name in ('fanasa-work-gitlab.service','fanasa-work-gitlab.timer'):
        target=Path('/etc/systemd/system')/name
        if target.exists() and 'Fanasa' not in target.read_text():raise RuntimeError('Conflicting service identity')
        shutil.copyfile(dest/name,target);target.chmod(0o644)
    run(['systemd-analyze','verify','/etc/systemd/system/fanasa-work-gitlab.service','/etc/systemd/system/fanasa-work-gitlab.timer'])
    active=ROOT/'current'
    previous=active.resolve() if active.is_symlink() else None
    if active.exists() and not active.is_symlink():raise RuntimeError('Unexpected active release path')
    nextlink=ROOT/'next-release'
    if nextlink.exists() or nextlink.is_symlink():raise RuntimeError('Pending activation requires reconciliation')
    nextlink.symlink_to(dest,target_is_directory=True);os.replace(nextlink,active)
    if previous:
        receipt=ROOT/'previous-release.json'
        receipt.write_text(json.dumps({'path':str(previous)}));receipt.chmod(0o600)
    run(['systemctl','daemon-reload'])
    try:
        run(['systemctl','start','fanasa-work-gitlab.service'])
    except RuntimeError:
        if previous:
            nextlink.symlink_to(previous,target_is_directory=True);os.replace(nextlink,active)
        raise
    run(['systemctl','enable','--now','fanasa-work-gitlab.timer'])
    print(json.dumps({'installed':True,'releaseSha256':digest,'runtimeUser':'fanasa-work-gitlab',
        'timer':'fanasa-work-gitlab.timer','result':json.loads(Path(config['state_directory'],'last-result.json').read_text())},ensure_ascii=False))

if __name__=='__main__':
    try:main()
    except Exception as e:
        print(json.dumps({'install':'failed','error':str(e) if isinstance(e,RuntimeError) else type(e).__name__}));raise SystemExit(1)
