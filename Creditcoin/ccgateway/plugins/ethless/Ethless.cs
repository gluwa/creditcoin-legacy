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
using System;
using System.Diagnostics;

namespace gethless
{
    class Ethless : ICCGatewayPlugin
    {
        private static int mConfirmationsExpected = 12;

        public bool Run(IConfiguration cfg, string[] command, out string msg)
        {
            Debug.Assert(command != null);
            Debug.Assert(command.Length > 0);
            if (command[0].Equals("verify"))
            {
                Debug.Assert(command.Length == 7);
                string sourceAddressString = command[1];
                string destinationAddressString = command[2];
                string proof = command[3];
                string destinationAmount = command[4];
                string txId = command[5];
                string unused = command[6];

#if DEBUG
                string confirmationsCount = cfg["confirmationsCount"];
                if (int.TryParse(confirmationsCount, out int parsedCount))
                {
                    mConfirmationsExpected = parsedCount;
                }
#endif

                string rpcUrl = cfg["rpc"];
                if (string.IsNullOrWhiteSpace(rpcUrl))
                {
                    msg = "ethless.rpc is not set";
                    return false;
                }

                var web3 = new Nethereum.Web3.Web3(rpcUrl);

                var tx = web3.Eth.Transactions.GetTransactionByHash.SendRequestAsync(txId).Result;
                if (tx == null)
                {
                    msg = "Failed to retrieve transaction info";
                    return false;
                }
                var txReceipt = web3.Eth.Transactions.GetTransactionReceipt.SendRequestAsync(txId).Result;
                if (txReceipt.Status.Value == 0)
                {
                    msg = "Invalid transaction: transaction status is 'failed'";
                    return false;
                }

                int confirmations = 0;
                if (tx.BlockNumber != null)
                {
                    var blockNumber = web3.Eth.Blocks.GetBlockNumber.SendRequestAsync().Result;
                    confirmations = (int)(blockNumber.Value - tx.BlockNumber.Value);
                }

                if (confirmations < mConfirmationsExpected)
                {
                    msg = "Invalid transaction: not enough confirmations";
                    return false;
                }

                var sourceAddressStringSegments = sourceAddressString.Split('@');
                sourceAddressString = sourceAddressStringSegments[1];
                var destinationAddressStringSegment = destinationAddressString.Split('@');
                destinationAddressString = destinationAddressStringSegment[1];

                if (!sourceAddressStringSegments[0].Equals(destinationAddressStringSegment[0]))
                {
                    msg = "Invalid transaction: source and destination ethless tokens (Gluwacoins) don't match";
                    return false;
                }

                if (!sourceAddressString.Equals(tx.From, System.StringComparison.OrdinalIgnoreCase))
                {
                    msg = "Invalid transaction: wrong sourceAddressString";
                    return false;
                }

                string ethlessContract = sourceAddressStringSegments[0];

                if (string.IsNullOrWhiteSpace(ethlessContract))
                {
                    msg = "Invalid ethless address";
                    return false;
                }

                if (!tx.To.Equals(ethlessContract, StringComparison.OrdinalIgnoreCase))
                {
                    msg = "transaction contract doesn't match ethlessContract";
                    return false;
                }

                string ethlessContractAbi = "[{\"constant\":false,\"inputs\":[{\"internalType\":\"address\",\"name\":\"_from\",\"type\":\"address\"},{\"internalType\":\"address\",\"name\":\"_to\",\"type\":\"address\"},{\"internalType\":\"uint256\",\"name\":\"_value\",\"type\":\"uint256\"},{\"internalType\":\"uint256\",\"name\":\"_fee\",\"type\":\"uint256\"},{\"internalType\":\"uint256\",\"name\":\"_nonce\",\"type\":\"uint256\"},{\"internalType\":\"bytes\",\"name\":\"_sig\",\"type\":\"bytes\"}],\"name\":\"transfer\",\"outputs\":[{\"internalType\":\"bool\",\"name\":\"success\",\"type\":\"bool\"}],\"payable\":false,\"stateMutability\":\"nonpayable\",\"type\":\"function\"}]";

                var contract = web3.Eth.GetContract(ethlessContractAbi, ethlessContract);
                var transfer = contract.GetFunction("transfer");
                var inputs = transfer.DecodeInput(tx.Input);
                Debug.Assert(inputs.Count == 6);

                var from = inputs[0].Result.ToString();
                if (!sourceAddressString.Equals(from, StringComparison.InvariantCultureIgnoreCase))
                {
                    msg = "Invalid transaction: wrong source";
                    return false;
                }

                var to = inputs[1].Result.ToString();
                if (!destinationAddressString.Equals(to, StringComparison.InvariantCultureIgnoreCase))
                {
                    msg = "Invalid transaction: wrong destination";
                    return false;
                }

                var value = inputs[2].Result.ToString();
                if (!destinationAmount.Equals(value))
                {
                    msg = "Invalid transaction: wrong amount";
                    return false;
                }

                var tag = inputs[4].Result.ToString();
                if (!tag.Equals("0x" + proof.Substring(10)))
                {
                    msg = "Invalid transaction: wrong proof";
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