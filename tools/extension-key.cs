// Generates the public key that pins the browser extension's ID, so the native host can name it.
//   dotnet run tools/extension-key.cs
// Paste "key" into extension/manifest.json and the ID into Fetchr.Core/Ipc.cs. The private key is
// thrown away: it's only needed to sign a .crx, and the store signs published builds itself.
using System.Security.Cryptography;

using var rsa = RSA.Create(2048);
var spki = rsa.ExportSubjectPublicKeyInfo();
var hash = SHA256.HashData(spki);
var id = string.Concat(hash.Take(16).SelectMany(b => new[] { b >> 4, b & 15 }).Select(n => (char)('a' + n)));
Console.WriteLine("key: " + Convert.ToBase64String(spki));
Console.WriteLine("id:  " + id);
