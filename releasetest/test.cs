using System.Text;
using SubSsh.Plugin.WebApi;
var (pem, pub) = KeyMaterial.Generate("t1");
Console.WriteLine("pem len=" + pem.Length);
var box = new SubSsh.Plugin.Crypto.AesGcmCryptoBox(Convert.FromHexString(new string('a', 64)));
var enc = await box.SealAsync(Encoding.UTF8.GetBytes(pem));
var dec = await box.OpenAsync(enc);
Console.WriteLine("enc=" + enc.Length + " roundtrip=" + (Encoding.UTF8.GetString(dec) == pem));
