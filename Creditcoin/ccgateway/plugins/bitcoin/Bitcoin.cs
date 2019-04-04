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

using ccplugin;
using Microsoft.Extensions.Configuration;
using NBitcoin;
using NBitcoin.RPC;
using System;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace gbitcoin
{
    class Bitcoin : ICCGatewayPlugin
    {
        private static string creditcoinUrl = "http://localhost:8008";

        private static int mConfirmationsExpected = 6;

        public bool Run(IConfiguration cfg, string[] command, out string msg)
        {
            Debug.Assert(command != null);
            Debug.Assert(command.Length > 0);
            if (command[0].Equals("verify"))
            {
                Debug.Assert(command.Length == 7);
                string sourceAddressString = command[1];
                string destinationAddressString = command[2];
                string orderId = command[3];
                string destinationAmount = command[4];
                string txId = command[5];
                string networkId = command[6];

                string rpcAddress = cfg["rpc"];
                if (string.IsNullOrWhiteSpace(rpcAddress))
                {
                    msg = "bitcoin.rpc is not set";
                    return false;
                }

                string credential = cfg["credential"];
                if (string.IsNullOrWhiteSpace(credential))
                {
                    msg = "bitcoin.credential is not set";
                    return false;
                }

#if DEBUG
                string confirmationsCount = cfg["confirmationsCount"];
                if (int.TryParse(confirmationsCount, out int parsedCount))
                {
                    mConfirmationsExpected = parsedCount;
                }
#endif

                Network network = ((networkId == "main") ? Network.Main : Network.TestNet);
                var rpcClient = new RPCClient(credential, new Uri(rpcAddress), network);
                if (!uint256.TryParse(txId, out var transactionId))
                {
                    msg = "Invalid transaction: transaction ID invalid";
                    return false;
                }

                var transactionInfoResponse = rpcClient.GetRawTransactionInfo(transactionId);
                if (transactionInfoResponse.Confirmations < mConfirmationsExpected)
                {
                    msg = "Invalid transaction: not enough confirmations";
                    return false;
                }

                if (transactionInfoResponse.Transaction.Outputs.Count < 2 || transactionInfoResponse.Transaction.Outputs.Count > 3)
                {
                    msg = "Invalid transaction: unexpected amount of output";
                    return false;
                }

                var destinationAddress = BitcoinAddress.Create(destinationAddressString, network);
                var outputCoins = transactionInfoResponse.Transaction.Outputs.AsCoins();
                var paymentCoin = outputCoins.SingleOrDefault(oc => oc.ScriptPubKey == destinationAddress.ScriptPubKey);
                if (paymentCoin == null)
                {
                    msg = "Invalid transaction: wrong destinationAddressString";
                    return false;
                }

                if (paymentCoin.TxOut.Value.Satoshi.ToString() != destinationAmount)
                {
                    msg = "Invalid transaction: wrong amount";
                    return false;
                }

                var bytes = Encoding.UTF8.GetBytes(orderId);
                var nullDataCoin = outputCoins.SingleOrDefault(oc => oc.ScriptPubKey == TxNullDataTemplate.Instance.GenerateScriptPubKey(bytes));
                if (nullDataCoin == null)
                {
                    msg = "Invalid transaction: wrong orderId";
                    return false;
                }

                if (nullDataCoin.TxOut.Value.CompareTo(Money.Zero) != 0)
                {
                    msg = "Invalid transaction: expecting a message";
                    return false;
                }

                var sourceAddress = BitcoinAddress.Create(sourceAddressString, network);
                var input = transactionInfoResponse.Transaction.Inputs[0];
                if (!Script.VerifyScript(input.ScriptSig, sourceAddress.ScriptPubKey, transactionInfoResponse.Transaction, 0))
                {
                    msg = "Invalid transaction: wrong sourceAddressString";
                    return false;
                }

                msg = null;
                return true;
            }

            msg = "Unknown command: " + command[0];
            return false;
        }
    }
}