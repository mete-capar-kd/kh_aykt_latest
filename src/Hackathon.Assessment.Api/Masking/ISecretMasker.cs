namespace Hackathon.Assessment.Api.Masking;

public interface ISecretMasker
{
    string Mask(string value);
}
