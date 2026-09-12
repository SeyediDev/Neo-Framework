"""Package a previously published MCP plus canonical repo skills."""
from pathlib import Path
import zipfile
comp=Path(__file__).resolve().parents[1]
repo=comp.parents[1]
publish=comp/'artifacts/mcp'
assert (publish/'Neo.Companion.Mcp.dll').is_file(), 'Publish the MCP before packaging.'
archive=comp/'artifacts/neo-companion.zip'
with zipfile.ZipFile(archive,'w',zipfile.ZIP_DEFLATED) as z:
    for file in sorted(publish.rglob('*')):
        if file.is_file(): z.write(file,'mcp/'+file.relative_to(publish).as_posix())
    for name in ['neo-feature','neo-telemetry','neo-doctor']:
        folder=repo/'.agents/skills'/name
        for file in sorted(folder.rglob('*')):
            if file.is_file(): z.write(file,'skills/'+name+'/'+file.relative_to(folder).as_posix())
    for name in ['README.md','LICENSE','docs/START-HERE.fa.md','docs/TELEMETRY.fa.md','docs/DOCTOR.fa.md','docs/VALIDATION.md']:
        z.write(comp/name,name)
    z.writestr('RUN.txt','Requires .NET 10 runtime. Run dotnet mcp/Neo.Companion.Mcp.dll through an MCP client. Set NEO_PROJECT_ROOT to your application. Copy each skills folder into the consuming repository .agents/skills directory.\n')
print(archive)
