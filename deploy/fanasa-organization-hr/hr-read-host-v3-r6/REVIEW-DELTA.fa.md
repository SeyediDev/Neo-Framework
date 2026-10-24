# HR3 r6 candidate review boundary

Predecessor: hr-read-host-v3-r5, manifest c2cd886eef4381b0e614dfb28b3efe7bb47c42c2ce98a2188796d670429a3318.

Compiled package C#, project, props and targets bytes are unchanged. Only candidate documentation, r6 artifact directories, predecessor integrity verification and regression tests differ. Immutable dependencies retain their pins. SOURCE-PACKAGE.json lists every changed/new/removed host input. No prior successful receipt is copied.

Before publication review the host delta, run all filesystem and actual-host suites on the immutable merged source, full CI release gates, archive verifier and OCI adapter tests. Keep r5 immutable. No runtime result follows from this new seal.
