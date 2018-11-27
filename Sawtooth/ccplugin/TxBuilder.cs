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
using System.IO;

namespace ccplugin
{
    public class TxBuilder : ITxBuilder
    {
        private Signer signer;
        private const string CREDITCOIN = "CREDITCOIN";
        private const string version = "1.0";
        private const string pluginsFolderName = "plugins";
        private static string prefix = CREDITCOIN.ToByteArray().ToSha512().ToHexString().Substring(0, 6);

        public TxBuilder(Signer signer)
        {
            this.signer = signer;
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
                folder = null;
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

            map.Add("i", DateTime.Now.Ticks); // id

            for (int i = 1; i < command.Length; ++i)
            {
                map.Add("p" + i.ToString(), command[i]); // params
            }

            var pubKeyHexStr = signer.GetPublicKey().ToHexString();
            var settings = new EncoderSettings()
            {
                BatcherPublicKey = pubKeyHexStr,
                SignerPublickey = pubKeyHexStr,
                FamilyName = CREDITCOIN,
                FamilyVersion = version
            };
            settings.Inputs.Add(prefix);
            settings.Outputs.Add(prefix);
            var encoder = new Encoder(settings, signer.GetPrivateKey());

            msg = null;
            return encoder.EncodeSingleTransaction(map.EncodeToBytes());
        }
    }
}
