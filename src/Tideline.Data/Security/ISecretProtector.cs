namespace Tideline.Data.Security;

/// <summary>Seals and opens secrets kept in the database (a webhook address, mail passwords, the SimpleFIN access address). The app backs it with its Data Protection keys.</summary>
public interface ISecretProtector
{
    string Protect(string plain);
    string Unprotect(string sealedText);
}
