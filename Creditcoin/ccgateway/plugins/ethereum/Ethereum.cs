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
using Nethereum.Signer;
using Sawtooth.Sdk.Client;
using System;
using System.Diagnostics;
using System.Net.Http;
using System.Numerics;
using System.Threading;

namespace gethereum
{
    class Ethereum : ICCGatewayPlugin
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
                string destinationAddressString = command[2];
                string destinationAmount = command[3];
                string sighash = command[4];
                string sourceAddressString = command[5];
                string networkId = command[6];

                // TODO disable confirmation count config for release build
                string confirmationsCount = cfg["confirmationsCount"];
                if (string.IsNullOrWhiteSpace(confirmationsCount))
                {
                    msg = "ethereum.confirmationsCount is not set";

                    return false;
                }
                if (!int.TryParse(confirmationsCount, out int confirmationsExpected))
                {
                    msg = "ethereum.confirmationsCount is not an int";

                    return false;
                }

                string rpcUrl = cfg["rpc"];
                if (string.IsNullOrWhiteSpace(rpcUrl))
                {
                    msg = "ethereum.rpc is not set";

                    return false;
                }

                var web3 = new Nethereum.Web3.Web3(rpcUrl);

                var tx = web3.Eth.Transactions.GetTransactionByHash.SendRequestAsync(txId).Result;
                int confirmations = 0;
                if (tx.BlockNumber != null)
                {
                    var blockNumber = web3.Eth.Blocks.GetBlockNumber.SendRequestAsync().Result;
                    confirmations = (int)(blockNumber.Value - tx.BlockNumber.Value);
                }

                if (confirmations < confirmationsExpected)
                {
                    msg = "Invalid transaction: not enough confirmations";
                    return false;
                }

                if (!sourceAddressString.Equals(tx.From, System.StringComparison.OrdinalIgnoreCase))
                {
                    msg = "Invalid transaction: wrong sourceAddressString";
                    return false;
                }

                if (networkId.Equals("creditcoin"))
                {
                    string creditcoinContract = cfg["creditcoinContract"];

                    if (string.IsNullOrWhiteSpace(creditcoinContract))
                    {
                        msg = "ethereum.creditcoinContract is not set";

                        return false;
                    }

                    string creditcoinContractAbi = cfg["creditcoinContractAbi"];
                    if (string.IsNullOrWhiteSpace(creditcoinContractAbi))
                    {
                        msg = "ethereum.creditcoinContractAbi is not set";

                        return false;
                    }

                    var contract = web3.Eth.GetContract(creditcoinContractAbi, creditcoinContract);
                    var burn = contract.GetFunction("exchange");
                    var inputs = burn.DecodeInput(tx.Input);
                    Debug.Assert(inputs.Count == 2);
                    var value = inputs[0].Result.ToString();
                    if (destinationAmount != value)
                    {
                        msg = "Invalid transaction: wrong amount";
                        return false;
                    }

                    var tag = inputs[1].Result.ToString();
                    if (!tag.Equals(sighash))
                    {
                        msg = "Invalid transaction: wrong sighash";
                        return false;
                    }
                }
                else
                {
                    if (destinationAmount != tx.Value.Value.ToString())
                    {
                        msg = "Invalid transaction: wrong amount";
                        return false;
                    }

                    if (tx.Input == null)
                    {
                        msg = "Invalid transaction: expecting data";
                        return false;
                    }

                    if (!sighash.StartsWith("0x"))
                    {
                        sighash = "0x" + sighash;
                    }
                    if (!tx.Input.Equals(sighash, System.StringComparison.OrdinalIgnoreCase))
                    {
                        msg = "Invalid transaction: wrong sighash";
                        return false;
                    }

                    if (!tx.To.Equals(destinationAddressString, System.StringComparison.OrdinalIgnoreCase))
                    {
                        msg = "Invalid transaction: wrong destinationAddressString";
                        return false;
                    }
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
                        msg = "The address doesn't match the ask order";
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
                        msg = "The address doesn't match the ask order";
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

                string secret = cfg["secret"];
                if (string.IsNullOrWhiteSpace(secret))
                {
                    msg = "ethereum.secret is not set";
                    return false;
                }
                var ethereumPrivateKey = secret;

                // TODO disable confirmation count config for release build
                string confirmationsCount = cfg["confirmationsCount"];
                if (string.IsNullOrWhiteSpace(confirmationsCount))
                {
                    msg = "ethereum.confirmationsCount is not set";
                    return false;
                }
                if (!int.TryParse(confirmationsCount, out int confirmationsExpected))
                {
                    msg = "ethereum.confirmationsCount is not an int";
                    return false;
                }

                string rpcUrl = cfg["rpc"];
                if (string.IsNullOrWhiteSpace(rpcUrl))
                {
                    msg = "ethereum.rpc is not set";
                    return false;
                }

                BigInteger fee;
                if (!BigInteger.TryParse(feeString, out fee))
                {
                    msg = "Invalid progress data";
                    return false;
                }

                var web3 = new Nethereum.Web3.Web3(rpcUrl);

                BigInteger transferAmount;
                if (!BigInteger.TryParse(amountString, out transferAmount) || transferAmount <= 0)
                {
                    msg = "Invalid amount";
                    return false;
                }

                string sourceAddress = EthECKey.GetPublicAddress(ethereumPrivateKey);

                if (!sourceAddress.Equals(addressFromString, StringComparison.OrdinalIgnoreCase))
                {
                    msg = "The deal is for a different client";
                    return false;
                }

                var txCount = web3.Eth.Transactions.GetTransactionCount.SendRequestAsync(sourceAddress).Result;
                TransactionSigner signer = new TransactionSigner();
                var gasLimit = web3.Eth.Transactions.EstimateGas.SendRequestAsync(new Nethereum.RPC.Eth.DTOs.CallInput(string.Empty, addressToString, new Nethereum.Hex.HexTypes.HexBigInteger(transferAmount))).Result;
                var gasPrice = web3.Eth.GasPrice.SendRequestAsync().Result;
                string txRaw = signer.SignTransaction(ethereumPrivateKey, addressToString, transferAmount + fee, txCount, gasPrice, gasLimit, string.Empty);
                string payTxId = web3.Eth.Transactions.SendRawTransaction.SendRequestAsync("0x" + txRaw).Result;

                while (true)
                {
                    var receipt = web3.Eth.TransactionManager.TransactionReceiptService.PollForReceiptAsync(payTxId).Result;
                    if (receipt.BlockNumber != null)
                    {
                        var blockNumber = web3.Eth.Blocks.GetBlockNumber.SendRequestAsync().Result;
                        if (blockNumber.Value - receipt.BlockNumber.Value >= confirmationsExpected)
                        {
                            break;
                        }
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