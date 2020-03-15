/*
    Copyright(c) 2018 Gluwa, Inc.

    This file is part of Creditcoin.

    Creditcoin is free software: you can redistribute it and/or modify
    it under the terms of the GNU Lesser General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.
    
    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
    GNU Lesser General Public License for more details.
    
    You should have received a copy of the GNU Lesser General Public License
    along with Creditcoin. If not, see <https://www.gnu.org/licenses/>.
*/

using PeterO.Cbor;
using Sawtooth.Sdk;
using Sawtooth.Sdk.Client;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Encoder = Sawtooth.Sdk.Client.Encoder;

namespace ccplugin
{
    public class TxBuilder : ITxBuilder
    {
        private Signer mSigner;
        private const string CREDITCOIN = "CREDITCOIN";
        private const string version = "1.4";
        private const string pluginsFolderName = "plugins";
        private static string prefix = CREDITCOIN.ToByteArray().ToSha512().ToHexString().Substring(0, 6);
        private const int SKIP_TO_GET_60 = 512 / 8 * 2 - 60; // 512 - hash size, 8 - bits in byte, 2 - hex digits for byte, 60 - merkle address length (70) without namespace length (6) and prexix length (4)

        public TxBuilder(Signer signer)
        {
            this.mSigner = signer;
        }

        public static string GetPluginsFolder(string root)
        {
            string folder = Path.Combine(root, pluginsFolderName);
            while (Path.GetPathRoot(root) != root && !Directory.Exists(folder))
            {
                root = Path.GetFullPath(Path.Combine(root, ".."));
                folder = Path.Combine(root, pluginsFolderName);
            }
            if (!Directory.Exists(folder))
            {
                folder = null;
            }
            return folder;
        }

        public byte[] BuildTx(string[] command, out string msg)
        {
            if (command.Length == 0)
            {
                msg = "Expecting a creditcoin command";
                return null;
            }

            var map = CBORObject.NewMap();
            map.Add("v", command[0]); // verb

            for (int i = 1; i < command.Length; ++i)
            {
                map.Add("p" + i.ToString(), command[i]); // params
            }

            var pubKeyHexStr = mSigner.GetPublicKey().ToHexString();
            var settings = new EncoderSettings()
            {
                BatcherPublicKey = pubKeyHexStr,
                SignerPublickey = pubKeyHexStr,
                FamilyName = CREDITCOIN,
                FamilyVersion = version
            };
            settings.Inputs.Add(prefix);
            settings.Outputs.Add(prefix);
            var encoder = new Encoder(settings, mSigner.GetPrivateKey());

            msg = null;
            return encoder.EncodeSingleTransaction(map.EncodeToBytes());
        }

        public static string getSighash(Signer signer)
        {
            var message = signer.GetPublicKey().ToHexString();
            Debug.Assert(message.Substring(0, 2) == "04");
            Debug.Assert(message.Length == 2 * (1 + 2 * 32));
            Debug.Assert(message.All("1234567890abcdef".Contains));
            var yLast = message.Substring(2 * (1 + 32 + 31), 2 * 1);
            int value = int.Parse(yLast, System.Globalization.NumberStyles.HexNumber);

            message = ((value % 2 == 0) ? "02" : "03") + message.Substring(2 * 1, 2 * 32);

            var data = Encoding.UTF8.GetBytes(message);
            using (SHA512 sha512 = new SHA512Managed())
            {
                var hash = sha512.ComputeHash(data);
                var hashString = string.Concat(Array.ConvertAll(hash, x => x.ToString("X2")));
                return hashString.Substring(SKIP_TO_GET_60).ToLower();
            }
        }

        public string getSighash()
        {
            return getSighash(mSigner);
        }
    }
}