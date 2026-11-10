using Microsoft.AspNetCore.DataProtection;

namespace Tams.Api.Enrollment;

/// <summary>
/// Encrypts fingerprint templates before they reach fingerprint_templates.template (PRD NFR-11,
/// Data Privacy Act). Uses ASP.NET Data Protection. IMPORTANT: the key ring must be persisted
/// (a mounted volume) or templates become unreadable after the container is rebuilt. The 1:N
/// matcher (kiosk checkout ticket) must call <see cref="Unprotect"/>; plaintext never leaves
/// the process and is never logged or returned by any endpoint.
/// </summary>
public static class TemplateProtector
{
    const string Purpose = "Tams.Biometrics.FingerprintTemplate.v1";

    static IDataProtector For(IServiceProvider sp) =>
        sp.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);

    public static byte[] Protect(IServiceProvider sp, byte[] plain) => For(sp).Protect(plain);
    public static byte[] Unprotect(IServiceProvider sp, byte[] cipher) => For(sp).Unprotect(cipher);
}
