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
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;

namespace cbitcoin
{
    public class Bitcoin : ICCClientPlugin
    {
        private const string name = "bitcoin";

        private const string STATE = "s";
        private const string NEW = "n";
        private const string FOUNDED = "f";
        private const string SOURCE_LEDGER = "sl";
        private const string SOURCE_ID = "si";
        private const string SOURCE_AMOUNT = "sa";
        private const string DESTINATION_LEDGER = "dl";
        private const string DESTINATION_ID = "di";
        private const string DESTINATION_AMOUNT = "da";

        private static int mConfirmationsExpected = 6;

        public bool Run(bool txid, IConfiguration cfg, HttpClient httpClient, ITxBuilder txBuilder, Dictionary<string, string> settings, string progressId, string pluginsFolder, string url, string[] command, out bool inProgress, out string msg)
        {
            Debug.Assert(command != null);
            if (command.Length < 2)
            {
                inProgress = false;
                msg = "invalid parameter count";
                return false;
            }

            if (command[0].Equals("registerTransfer", StringComparison.OrdinalIgnoreCase))
            {
                // bitcoin RegisterTransfer srcAddressId dstAddressId orderId amount sourceTxId

                string progress = Path.Combine(pluginsFolder, $"{name}_progress{progressId}.txt");

                inProgress = false;

                if (command.Length != 4)
                {
                    msg = "invalid parameter count";
                    return false;
                }

                string gainString = command[1];
                string orderId = command[2];
                string sourceTxIdString = command[3];

                string secret = cfg["secret"];
                if (string.IsNullOrWhiteSpace(secret))
                {
                    msg = "bitcoin.secret is not set";
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

#if DEBUG
                string confirmationsCount = cfg["confirmationsCount"];
                if (int.TryParse(confirmationsCount, out int parsedCount))
                {
                    mConfirmationsExpected = parsedCount;
                }
#endif

                string feeString = cfg["fee"];
                if (string.IsNullOrWhiteSpace(feeString))
                {
                    msg = "bitcoin.fee is not set";
                    return false;
                }
                if (!int.TryParse(feeString, out int fee))
                {
                    msg = "bitcoin.fee is not an int";
                    return false;
                }

                var bitcoinPrivateKey = new BitcoinSecret(secret);
                var network = bitcoinPrivateKey.Network;
                var rpcClient = new RPCClient(credential, new Uri(rpcAddress), network);

                string srcAddressId;
                string dstAddressId;
                string amountString;

                var protobuf = RpcHelper.ReadProtobuf(httpClient, $"{url}/state/{orderId}", out msg);
                if (protobuf == null)
                {
                    msg = "failed to extract address data through RPC";
                    return false;
                }
                if (orderId.StartsWith(RpcHelper.creditCoinNamespace + RpcHelper.dealOrderPrefix))
                {
                    var dealOrder = DealOrder.Parser.ParseFrom(protobuf);
                    if (gainString.Equals("0"))
                    {
                        srcAddressId = dealOrder.SrcAddress;
                        dstAddressId = dealOrder.DstAddress;
                    }
                    else
                    {
                        dstAddressId = dealOrder.SrcAddress;
                        srcAddressId = dealOrder.DstAddress;
                    }
                    amountString = dealOrder.Amount;
                }
                else if (orderId.StartsWith(RpcHelper.creditCoinNamespace + RpcHelper.repaymentOrderPrefix))
                {
                    var repaymentOrder = RepaymentOrder.Parser.ParseFrom(protobuf);
                    if (gainString.Equals("0"))
                    {
                        srcAddressId = repaymentOrder.SrcAddress;
                        dstAddressId = repaymentOrder.DstAddress;
                    }
                    else
                    {
                        dstAddressId = repaymentOrder.SrcAddress;
                        srcAddressId = repaymentOrder.DstAddress;
                    }
                    amountString = repaymentOrder.Amount;
                }
                else
                {
                    msg = "unexpected referred order";
                    return false;
                }

                string payTxIdHash;
                uint256 payTxId;
                if (File.Exists(progress))
                {
                    Console.WriteLine("Found unfinished action, retrying...");
                    payTxIdHash = File.ReadAllText(progress);
                    if (!uint256.TryParse(payTxIdHash, out payTxId))
                    {
                        msg = "corrupted progress file";
                        return false;
                    }
                }
                else
                {
                    protobuf = RpcHelper.ReadProtobuf(httpClient, $"{url}/state/{srcAddressId}", out msg);
                    if (protobuf == null)
                    {
                        msg = "failed to extract address data through RPC";
                        return false;
                    }

                    var srcAddress = Address.Parser.ParseFrom(protobuf);

                    protobuf = RpcHelper.ReadProtobuf(httpClient, $"{url}/state/{dstAddressId}", out msg);
                    if (protobuf == null)
                    {
                        msg = "failed to extract address data through RPC";
                        return false;
                    }

                    var dstAddress = Address.Parser.ParseFrom(protobuf);

                    long transferAmount;
                    if (!long.TryParse(amountString, out transferAmount) || transferAmount <= 0)
                    {
                        msg = "Invalid amount";
                        return false;
                    }
                    long gain;
                    if (!long.TryParse(gainString, out gain))
                    {
                        msg = "Invalid amount";
                        return false;
                    }
                    if (transferAmount + gain < transferAmount)
                    {
                        msg = "Overflow";
                        return false;
                    }

                    transferAmount = transferAmount + gain;

                    if (transferAmount < 0)
                    {
                        msg = "Invalid amount";
                        return false;
                    }

                    if (transferAmount + fee < transferAmount)
                    {
                        msg = "Overflow";
                        return false;
                    }

                    if (!srcAddress.Blockchain.Equals(name) || !dstAddress.Blockchain.Equals(name))
                    {
                        msg = $"bitcoin RegisterTransfer can only transfer bitcoins.\nThis source is registered for {srcAddress.Blockchain} and destination for {dstAddress.Blockchain}";
                        return false;
                    }

                    var sourceAddress = bitcoinPrivateKey.GetAddress();
                    if (!sourceAddress.ToString().Equals(srcAddress.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        msg = "The deal is for a different client";
                        return false;
                    }

#if RELEASE
                    if (network != NBitcoin.Network.Main)
                    {
                        msg = "bitcoin.secret is not defined for main network";
                        return false;
                    }
#endif

                    var sourceTxId = uint256.Parse(sourceTxIdString);
                    var transactionResponse = rpcClient.GetRawTransaction(sourceTxId);

                    //TODO: fix (assuming that the transaction has one output intended for the private key with the exact amount)
                    var receivedCoins = transactionResponse.Outputs.AsCoins();
                    OutPoint outPointToSpend = null;
                    TxOut outTxToSpend = null;
                    foreach (var coin in receivedCoins)
                    {
                        if (coin.TxOut.ScriptPubKey == bitcoinPrivateKey.ScriptPubKey)
                        {
                            outPointToSpend = coin.Outpoint;
                            outTxToSpend = coin.TxOut;
                            long amount = coin.Amount.Satoshi;
                            if (amount < transferAmount + fee)
                            {
                                msg = $"Invalid transaction - needed: {transferAmount} + {fee}, has: {amount.ToString()}";
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

                    var addressFrom = BitcoinAddress.Create(srcAddress.Value, network);
                    var addressTo = BitcoinAddress.Create(dstAddress.Value, network);
                    var message = orderId;
                    var bytes = Encoding.UTF8.GetBytes(message);

                    var bitcoinTransactionBuilder = new TransactionBuilder();

                    var transaction = bitcoinTransactionBuilder
                        .AddCoins(new Coin(outPointToSpend, outTxToSpend))
                        .AddKeys(bitcoinPrivateKey)
                        .Send(addressTo.ScriptPubKey, transferAmount)
                        .Send(TxNullDataTemplate.Instance.GenerateScriptPubKey(bytes), Money.Zero)
                        .SendFees(new Money(fee, MoneyUnit.Satoshi))
                        .SetChange(addressFrom.ScriptPubKey)
                        .BuildTransaction(true);

                    if (!bitcoinTransactionBuilder.Verify(transaction))
                    {
                        msg = "failed verify transaction";
                        return false;
                    }

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

                    payTxIdHash = payTxId.ToString();
                    File.WriteAllText(progress, payTxIdHash);
                }

                inProgress = true;
                while (true)
                {
                    var transactionResponse = rpcClient.GetRawTransactionInfo(payTxId);
                    if (transactionResponse != null && transactionResponse.BlockHash != null && transactionResponse.Confirmations >= mConfirmationsExpected)
                    {
                        break;
                    }

                    Thread.Sleep(1000);
                }

                command = new string[] { command[0], gainString, orderId, payTxIdHash };
                var tx = txBuilder.BuildTx(command, out msg);
                Debug.Assert(tx != null);
                Debug.Assert(msg == null);

                var content = new ByteArrayContent(tx);
                content.Headers.Add("Content-Type", "application/octet-stream");

                msg = RpcHelper.CompleteBatch(httpClient, url, "batches", content, txid);

                File.Delete(progress);
                inProgress = false;

                return true;
            }
            else
            {
                inProgress = false;
                msg = "Unknown command: " + command[0];
                return false;
            }
        }
    }
}