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
using Sawtooth.Sdk.Client;
using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;

namespace gbitcoin
{
    class Bitcoin : ICCGatewayPlugin
    {
        private static string creditcoinUrl = "http://localhost:8008";

        public bool Run(IConfiguration cfg, string signerHexStr, string[] command, out string msg)
        {
            Debug.Assert(command != null);
            Debug.Assert(command.Length > 0);
            if (command[0].Equals("verify"))
            {
                Debug.Assert(command.Length == 7);
                string txId = command[1];
                string destinationAdressString = command[2];
                string destinationAmount = command[3];
                string sighash = command[4];
                string sourceAddressString = command[5];
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

                // TODO disable confirmation customization for release build
                string confirmationsCount = cfg["confirmationsCount"];
                if (string.IsNullOrWhiteSpace(confirmationsCount))
                {
                    msg = "bitcoin.confirmationsCount is not set";
                    return false;
                }
                var confirmationsExpected = 1;
                if (!int.TryParse(confirmationsCount, out confirmationsExpected))
                {
                    msg = "bitcoin.confirmationsCount is not an int";
                    return false;
                }

                Network network = ((networkId == "1") ? Network.Main : Network.TestNet);
                var rpcClient = new RPCClient(credential, new Uri(rpcAddress), network);
                if (!uint256.TryParse(txId, out var transactionId))
                {
                    msg = "Invalid transaction: transaction ID invalid";
                    return false;
                }

                var transactionInfoResponse = rpcClient.GetRawTransactionInfo(transactionId);
                if (transactionInfoResponse.Confirmations < confirmationsExpected)
                {
                    msg = "Invalid transaction: not enough confirmations";
                    return false;
                }

                if (transactionInfoResponse.Transaction.Outputs.Count < 2 || transactionInfoResponse.Transaction.Outputs.Count > 3)
                {
                    msg = "Invalid transaction: unexpected amount of output";
                    return false;
                }

                var destinationAddress = BitcoinAddress.Create(destinationAdressString, network);
                var outputCoins = transactionInfoResponse.Transaction.Outputs.AsCoins();
                var paymentCoin = outputCoins.SingleOrDefault(oc => oc.ScriptPubKey == destinationAddress.ScriptPubKey);
                if (paymentCoin == null)
                {
                    msg = "Invalid transaction: wrong destinationAdressString";
                    return false;
                }

                if (paymentCoin.TxOut.Value.Satoshi.ToString() != destinationAmount.ToString())
                {
                    msg = "Invalid transaction: wrong amount";
                    return false;
                }

                var bytes = Encoding.UTF8.GetBytes(sighash);
                var nullDataCoin = outputCoins.SingleOrDefault(oc => oc.ScriptPubKey == TxNullDataTemplate.Instance.GenerateScriptPubKey(bytes));
                if (nullDataCoin == null)
                {
                    msg = "Invalid transaction: wrong sighash";
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
            else if (command[0].Equals("unlock"))
            {
                var ccSigner = new Signer(RpcHelper.HexToBytes(signerHexStr));
                var txBuilder = new TxBuilder(ccSigner);

                Debug.Assert(command.Length == 5);
                string kind = command[1];
                string addressFromString = command[2];

                HttpClient httpClient = new HttpClient();

                string txFrom;
                string amountString;
                string feeString;
                string addressToString;
                string networkId;

                string ccCommand;

                if (kind.Equals("funds"))
                {
                    string dealOrderId = command[3];
                    string addressToUnlockFundsTo = command[4];

                    var protobuf = RpcHelper.ReadProtobuf(httpClient, $"{creditcoinUrl}/state/{dealOrderId}", out msg);
                    if (protobuf == null)
                    {
                        return false;
                    }
                    var dealOrder = DealOrder.Parser.ParseFrom(protobuf);
                    protobuf = RpcHelper.ReadProtobuf(httpClient, $"{creditcoinUrl}/state/{dealOrder.AskOrderId}", out msg);
                    if (protobuf == null)
                    {
                        return false;
                    }
                    var askOrder = AskOrder.Parser.ParseFrom(protobuf);
                    protobuf = RpcHelper.ReadProtobuf(httpClient, $"{creditcoinUrl}/state/{askOrder.TransferId}", out msg);
                    if (protobuf == null)
                    {
                        return false;
                    }
                    var transfer = Transfer.Parser.ParseFrom(protobuf);
                    protobuf = RpcHelper.ReadProtobuf(httpClient, $"{creditcoinUrl}/state/{addressToUnlockFundsTo}", out msg);
                    if (protobuf == null)
                    {
                        return false;
                    }
                    var address = Address.Parser.ParseFrom(protobuf);
                    if (!askOrder.Sighash.Equals(address.Sighash))
                    {
                        msg = "The adress doesn't match the ask order";
                        return false;
                    }
                    txFrom = transfer.Txid;
                    amountString = transfer.Amount;
                    feeString = transfer.Fee;
                    addressToString = address.Address_;
                    networkId = transfer.Network;
                    ccCommand = "UnlockFunds";
                }
                else if (kind.Equals("collateral"))
                {
                    string repaymentOrderId = command[3];
                    string addressToUnlockCollateralsTo = command[4];

                    var protobuf = RpcHelper.ReadProtobuf(httpClient, $"{creditcoinUrl}/state/{repaymentOrderId}", out msg);
                    if (protobuf == null)
                    {
                        return false;
                    }
                    var repaymentOrder = RepaymentOrder.Parser.ParseFrom(protobuf);
                    protobuf = RpcHelper.ReadProtobuf(httpClient, $"{creditcoinUrl}/state/{repaymentOrder.DealId}", out msg);
                    if (protobuf == null)
                    {
                        return false;
                    }
                    var dealOrder = DealOrder.Parser.ParseFrom(protobuf);
                    protobuf = RpcHelper.ReadProtobuf(httpClient, $"{creditcoinUrl}/state/{dealOrder.AskOrderId}", out msg);
                    if (protobuf == null)
                    {
                        return false;
                    }
                    var askOrder = AskOrder.Parser.ParseFrom(protobuf);
                    protobuf = RpcHelper.ReadProtobuf(httpClient, $"{creditcoinUrl}/state/{askOrder.TransferId}", out msg);
                    if (protobuf == null)
                    {
                        return false;
                    }
                    var transfer = Transfer.Parser.ParseFrom(protobuf);
                    protobuf = RpcHelper.ReadProtobuf(httpClient, $"{creditcoinUrl}/state/{addressToUnlockCollateralsTo}", out msg);
                    if (protobuf == null)
                    {
                        return false;
                    }
                    var address = Address.Parser.ParseFrom(protobuf);
                    if (!askOrder.Sighash.Equals(address.Sighash))
                    {
                        msg = "The adress doesn't match the ask order";
                        return false;
                    }
                    txFrom = transfer.Txid;
                    amountString = transfer.Amount;
                    feeString = transfer.Fee;
                    addressToString = address.Address_;
                    networkId = transfer.Network;
                    ccCommand = "UnlockCollateral";
                }
                else
                {
                    msg = "unknown unlock kind";
                    return false;
                }

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

                string secret = cfg["secret"];
                if (string.IsNullOrWhiteSpace(secret))
                {
                    msg = "bitcoin.secret is not set";
                    return false;
                }

                // TODO disable confirmation config for release build
                string confirmationsCount = cfg["confirmationsCount"];
                if (string.IsNullOrWhiteSpace(confirmationsCount))
                {
                    msg = "bitcoin.confirmationsCount is not set";
                    return false;
                }
                var confirmationsExpected = 1;
                if (!int.TryParse(confirmationsCount, out confirmationsExpected))
                {
                    msg = "bitcoin.confirmationsCount is not an int";
                    return false;
                }

                Network network = ((networkId == "1") ? Network.Main : Network.TestNet);
                var rpcClient = new RPCClient(credential, new Uri(rpcAddress), network);
                var transactionId = uint256.Parse(txFrom);

                var bitcoinPrivateKey = new BitcoinSecret(secret);
                if (bitcoinPrivateKey.Network != network)
                {
                    msg = "Mismatching networks";
                    return false;
                }

                var transactionResponse = rpcClient.GetRawTransaction(transactionId);

                Money transferAmount;
                if (!Money.TryParse(amountString, out transferAmount) || transferAmount <= 0)
                {
                    msg = "Invalid amount";
                    return false;
                }

                if (!int.TryParse(feeString, out int fee))
                {
                    msg = "bitcoin.fee is not an int";
                    return false;
                }

                var receivedCoins = transactionResponse.Outputs.AsCoins();
                OutPoint outPointToSpend = null;
                TxOut outTxToSpend = null;
                Money amount = null;
                foreach (var coin in receivedCoins)
                {
                    if (coin.TxOut.ScriptPubKey == bitcoinPrivateKey.ScriptPubKey)
                    {
                        outPointToSpend = coin.Outpoint;
                        outTxToSpend = coin.TxOut;
                        amount = (Money)coin.Amount;
                        if (amount.CompareTo(transferAmount + fee * 2) < 0)
                        {
                            msg = $"Invalid transaction - needed: {transferAmount}, has: {amount.ToString()}";
                            return false;
                        }
                        break;
                    }
                }
                if (outPointToSpend == null)
                {
                    msg = "Invalid transaction - no outputs that the client can spend";
                    return false;
                }

                var addressFrom = BitcoinAddress.Create(addressFromString, network);
                var addressTo = BitcoinAddress.Create(addressToString, network);

                var bitcoinTransactionBuilder = new TransactionBuilder();

                var transaction = bitcoinTransactionBuilder
                    .AddCoins(new Coin(outPointToSpend, outTxToSpend))
                    .AddKeys(bitcoinPrivateKey)
                    .Send(addressTo.ScriptPubKey, transferAmount)
                    .SendFees(new Money(fee, MoneyUnit.Satoshi))
                    .SetChange(addressFrom.ScriptPubKey)
                    .BuildTransaction(true);

                if (!bitcoinTransactionBuilder.Verify(transaction))
                {
                    msg = "failed verify transaction";
                    return false;
                }

                uint256 payTxId;
                try
                {
                    payTxId = rpcClient.SendRawTransaction(transaction);
                }
                catch (RPCException e)
                {
                    msg = $"failed to broadcast - error: {e.RPCCode}, reason: {e.RPCCodeMessage}";
                    return false;
                }

                if (payTxId == null)
                {
                    msg = "failed to broadcast - unknown error";
                    return false;
                }

                while (true) //TODO: this may lock for a very long time or forever, fix ---------------------------------------------------------------------------------------------------------------------------------------
                {
                    var transactionInfo = rpcClient.GetRawTransactionInfo(payTxId);
                    if (transactionInfo != null && transactionInfo.BlockHash != null && transactionInfo.Confirmations >= confirmationsExpected)
                    {
                        break;
                    }

                    Thread.Sleep(1000);
                }

                command = new string[] { ccCommand, command[3], command[4] };
                var tx = txBuilder.BuildTx(command, out msg);
                Debug.Assert(tx != null);
                Debug.Assert(msg == null);
                var content = new ByteArrayContent(tx);
                content.Headers.Add("Content-Type", "application/octet-stream");
                msg = RpcHelper.CompleteBatch(httpClient, $"{creditcoinUrl}/batches", content);
            }

            msg = "Unknown command: " + command[0];
            return false;
        }
    }
}