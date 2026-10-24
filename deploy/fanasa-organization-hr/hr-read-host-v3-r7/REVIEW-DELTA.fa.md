# r7 review boundary

Predecessor: hr-read-host-v3-r6, manifest 4cdfe260b6706bd33f857288b76f58867ce0d227df7dd5d29c2b49b1ec8af585. Predecessor own files and immutable dependencies were verified before construction. r6/r5 and their manifests remain unchanged.

The explicit host delta is 13 changed financial files and six additions, with no removals; SOURCE-PACKAGE.json contains the complete 148-input closed host inventory. Each changed/new financial source matches the accepted central-integration source manifest. Existing Program.cs and PCC ContractPolicyStore.cs are unchanged, so the HR3 compatibility copies and source guards are retained unchanged.

Package changes are limited to this README/review, r7 artifact names, the immediate predecessor pin and corresponding filesystem regression fixtures. No C#/project/props/targets source is altered, no receipt is copied into the package, and no prior success is inherited. Source candidates start unverified, runtime disabled.

Financial invariants requiring acceptance include atomic unit.save/charge/receipt/outbox, PAYG insufficient-credit rollback, replay/conflict behavior, immutable historical invoice values, bounded credit notes/reservations, live tenant/subject/grant/CSRF checks and exact decimal transport. Accepted standard-host local provenance is external; the fresh HR3 opt-in build and actual-host baseline must run on this exact manifest. Trusted CI/source selection, archive/OCI verification and live acceptance remain pending independently.
