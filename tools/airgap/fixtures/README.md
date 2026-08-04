# Offline recovery smoke fixture

`floss-recovery-smoke.exe` is a benign Windows x64 console program built from
`floss-recovery-smoke.c`. It XOR-decodes `FLOSS_RECOVERY_OK` into a stack buffer
and prints it. It performs no file, registry, process, or network operations.

The executable exists only to make the complete offline release smoke test
prove that Magika routed a PE file and FLOSS returned an attributable recovered
string. Rebuild it from a Visual Studio x64 developer shell with:

```powershell
cl.exe /nologo /Od /MT /W4 /WX /Fe:floss-recovery-smoke.exe floss-recovery-smoke.c
```

The checked-in SHA-256 is recorded in `floss-recovery-smoke.sha256`.
