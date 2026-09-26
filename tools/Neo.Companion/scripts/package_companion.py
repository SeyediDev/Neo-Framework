"""Package a previously published MCP plus canonical repo skills."""
from pathlib import Path
import zipfile
comp=Path(__file__).resolve().parents[1]
repo=comp.parents[1]
publish=comp/'artifacts/mcp'
cli=comp/'artifacts/cli'
assert (publish/'Neo.Companion.Mcp.dll').is_file(), 'Publish the MCP before packaging.'
assert (cli/'Neo.Companion.Cli.dll').is_file(), 'Publish the CLI before packaging.'
archive=comp/'artifacts/neo-companion.zip'
with zipfile.ZipFile(archive,'w',zipfile.ZIP_DEFLATED) as z:
    for file in sorted(publish.rglob('*')):
        if file.is_file(): z.write(file,'mcp/'+file.relative_to(publish).as_posix())
    for file in sorted(cli.rglob('*')):
        if file.is_file(): z.write(file,'cli/'+file.relative_to(cli).as_posix())
    for file in sorted((comp/'artifacts/packages').glob('Neo.Companion.Cli.*.nupkg')):
        z.write(file,'packages/'+file.name)
    for name in ['neo-feature','neo-telemetry','neo-doctor']:
        folder=repo/'.agents/skills'/name
        for file in sorted(folder.rglob('*')):
            if file.is_file(): z.write(file,'skills/'+name+'/'+file.relative_to(folder).as_posix())
    for name in ['README.md','LICENSE','docs/START-HERE.fa.md','docs/TELEMETRY.fa.md','docs/DOCTOR.fa.md','docs/GENERIC-CRUD.fa.md','docs/CRUD-RESOURCES.fa.md','docs/FEATURE-GENERATOR.fa.md','docs/crud-resource-backlog.json','docs/MESSAGING.fa.md','docs/SAGA.fa.md','docs/EVENT-DELIVERY.fa.md','docs/event-delivery-backlog.json','docs/VALIDATION.md']:
        z.write(comp/name,name)
    # Keep guide links useful offline; source samples still require the matching Neo checkout.
    for file in sorted((comp/'samples').rglob('*')):
        if file.is_file() and not {'bin','obj'}.intersection(file.parts) and file.suffix in {'.cs','.csproj','.json','.yaml','.md'}:
            z.write(file,file.relative_to(comp).as_posix())
    z.writestr('RUN.txt','Requires .NET 10 runtime; generated apps need the SDK and matching Neo checkout. Run dotnet mcp/Neo.Companion.Mcp.dll through an MCP client. Set NEO_PROJECT_ROOT to your application. Run dotnet cli/Neo.Companion.Cli.dll --help for feature generation. Copy each skills folder into the consuming repository .agents/skills directory.\n')
with zipfile.ZipFile(archive) as z:
    required={'cli/Neo.Companion.Cli.dll','mcp/Neo.Companion.Mcp.dll','mcp/examples/CrudResourceDemo/ProductResource.cs','docs/CRUD-RESOURCES.fa.md','docs/FEATURE-GENERATOR.fa.md','skills/neo-feature/references/crud-resources.md'}
    assert required.issubset(z.namelist()), 'Incomplete Companion package'
print(archive)
