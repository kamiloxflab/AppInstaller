using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace AppInstaller.Core
{
    /// <summary>
    /// Weryfikacja podpisów katalogu/manifestu aktualizacji (RSA-3072, SHA-256, PKCS#1 v1.5),
    /// skrótów plików oraz podpisów Authenticode pobranych instalatorów.
    /// </summary>
    public static class Crypto
    {
        static string _publicKeyXml;
        static bool _loaded;

        /// <summary>Klucz publiczny osadzony w exe (keys/public.xml). Bez niego zdalny katalog i aktualizacje są wyłączone.</summary>
        public static string PublicKeyXml
        {
            get
            {
                if (_loaded) return _publicKeyXml;
                _loaded = true;
                try
                {
                    using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("AppInstaller.public.xml"))
                    {
                        if (s == null) return null;
                        using (var r = new StreamReader(s)) _publicKeyXml = r.ReadToEnd().Trim();
                    }
                    if (!_publicKeyXml.Contains("<Modulus>")) _publicKeyXml = null;
                }
                catch { _publicKeyXml = null; }
                return _publicKeyXml;
            }
        }

        public static bool HasPublicKey { get { return PublicKeyXml != null; } }

        public static bool VerifySignature(byte[] data, string signatureBase64)
        {
            return VerifySignature(data, signatureBase64, PublicKeyXml);
        }

        public static bool VerifySignature(byte[] data, string signatureBase64, string publicKeyXml)
        {
            if (publicKeyXml == null || data == null || string.IsNullOrWhiteSpace(signatureBase64)) return false;
            try
            {
                var sig = Convert.FromBase64String(signatureBase64.Trim());
                using (var rsa = new RSACryptoServiceProvider())
                {
                    rsa.PersistKeyInCsp = false;
                    rsa.FromXmlString(publicKeyXml);
                    if (rsa.KeySize < 2048) return false;
                    return rsa.VerifyData(data, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Signature check error: " + ex.Message);
                return false;
            }
        }

        /// <summary>Tylko dla narzędzi/testów — w aplikacji nie ma klucza prywatnego.</summary>
        public static string Sign(byte[] data, string privateKeyXml)
        {
            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.PersistKeyInCsp = false;
                rsa.FromXmlString(privateKeyXml);
                return Convert.ToBase64String(rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            }
        }

        public static string FileHash(string path, string algorithm)
        {
            using (var h = HashAlgorithm.Create(algorithm))
            using (var fs = File.OpenRead(path))
            {
                var b = h.ComputeHash(fs);
                var sb = new StringBuilder(b.Length * 2);
                foreach (var x in b) sb.Append(x.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>Porównuje skrót pliku; "sha256:abcd..." lub sama heksa (długość wybiera algorytm).</summary>
        public static bool CheckHash(string path, string expected)
        {
            if (string.IsNullOrWhiteSpace(expected)) return true;
            var e = expected.Trim().ToLowerInvariant();
            string algo;
            int colon = e.IndexOf(':');
            if (colon > 0) { algo = e.Substring(0, colon); e = e.Substring(colon + 1); }
            else algo = e.Length == 128 ? "sha512" : e.Length == 96 ? "sha384" : "sha256";
            string name = algo == "sha512" ? "SHA512" : algo == "sha384" ? "SHA384" : algo == "sha256" ? "SHA256" : null;
            if (name == null) return true; // nieznany algorytm — nie blokujemy, ale logujemy
            var actual = FileHash(path, name);
            return string.Equals(actual, e, StringComparison.OrdinalIgnoreCase);
        }

        // ---------------- Authenticode ----------------

        public enum SignatureState { Valid, Unsigned, Invalid, Unavailable }

        public static SignatureState VerifyAuthenticode(string path, out string subject)
        {
            subject = null;
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return SignatureState.Unavailable;
            int rc;
            try { rc = WinTrust.Verify(path); }
            catch { return SignatureState.Unavailable; }
            try
            {
                var cert = X509Certificate.CreateFromSignedFile(path);
                subject = cert.Subject;
            }
            catch { }
            if (rc == 0) return SignatureState.Valid;
            if (rc == unchecked((int)0x800B0100)) return SignatureState.Unsigned; // TRUST_E_NOSIGNATURE
            return SignatureState.Invalid;
        }

        static class WinTrust
        {
            static readonly Guid ActionGenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            struct WINTRUST_FILE_INFO
            {
                public uint cbStruct;
                public IntPtr pcwszFilePath;
                public IntPtr hFile;
                public IntPtr pgKnownSubject;
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            struct WINTRUST_DATA
            {
                public uint cbStruct;
                public IntPtr pPolicyCallbackData;
                public IntPtr pSIPClientData;
                public uint dwUIChoice;
                public uint fdwRevocationChecks;
                public uint dwUnionChoice;
                public IntPtr pFile;
                public uint dwStateAction;
                public IntPtr hWVTStateData;
                public IntPtr pwszURLReference;
                public uint dwProvFlags;
                public uint dwUIContext;
                public IntPtr pSignatureSettings;
            }

            [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
            static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, IntPtr data);

            public static int Verify(string path)
            {
                IntPtr pPath = Marshal.StringToHGlobalUni(path);
                IntPtr pFile = IntPtr.Zero, pData = IntPtr.Zero;
                try
                {
                    var fi = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)), pcwszFilePath = pPath };
                    pFile = Marshal.AllocHGlobal(Marshal.SizeOf(fi));
                    Marshal.StructureToPtr(fi, pFile, false);
                    var wd = new WINTRUST_DATA
                    {
                        cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_DATA)),
                        dwUIChoice = 2,           // WTD_UI_NONE
                        fdwRevocationChecks = 0,  // WTD_REVOKE_NONE
                        dwUnionChoice = 1,        // WTD_CHOICE_FILE
                        pFile = pFile,
                        dwStateAction = 1,        // WTD_STATEACTION_VERIFY
                        dwProvFlags = 0x00000010  // WTD_REVOCATION_CHECK_NONE
                    };
                    pData = Marshal.AllocHGlobal(Marshal.SizeOf(wd));
                    Marshal.StructureToPtr(wd, pData, false);
                    int rc = WinVerifyTrust(new IntPtr(-1), ActionGenericVerifyV2, pData);
                    // zamknij stan
                    wd = (WINTRUST_DATA)Marshal.PtrToStructure(pData, typeof(WINTRUST_DATA));
                    wd.dwStateAction = 2; // WTD_STATEACTION_CLOSE
                    Marshal.StructureToPtr(wd, pData, true);
                    WinVerifyTrust(new IntPtr(-1), ActionGenericVerifyV2, pData);
                    return rc;
                }
                finally
                {
                    if (pData != IntPtr.Zero) Marshal.FreeHGlobal(pData);
                    if (pFile != IntPtr.Zero) Marshal.FreeHGlobal(pFile);
                    Marshal.FreeHGlobal(pPath);
                }
            }
        }
    }
}
