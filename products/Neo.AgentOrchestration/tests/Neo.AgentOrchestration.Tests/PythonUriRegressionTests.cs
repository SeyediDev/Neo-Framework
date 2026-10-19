using Neo.AgentOrchestration.Infrastructure.LanguageTools;
using Xunit;
public sealed class PythonUriRegressionTests {
 [Fact] public void EncodedDriveResolvesButForeignAndNonlocalRemainRejected() {
   if (!OperatingSystem.IsWindows()) return;
   var root=Path.Combine(Path.GetTempPath(),"neo-lsp-uri-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
   var file=Path.Combine(root,"python_healthy.py"); File.WriteAllText(file,"x = 1");
   var canonical=new Uri(file).AbsoluteUri;
   var drive=Path.GetPathRoot(file)![0]; var encoded=canonical.Replace($"{drive}:",$"{char.ToLowerInvariant(drive)}%3A");
   Assert.Equal(file,WorkspacePath.ResolveUri(root,encoded),StringComparer.OrdinalIgnoreCase);
   foreach(var bad in new[]{"https://example.com/a.py","file://server/share/a.py", canonical+"?x=1",canonical+"#fragment",new Uri(Path.GetFullPath(".build/lsp-validation/python-healthy-config.json")).AbsoluteUri,canonical+"%3Astream"})
     Assert.ThrowsAny<Exception>(()=>WorkspacePath.ResolveUri(root,bad));
 }
}
