pragma solidity ^0.5.1;

interface CreditcoinErc20 {
    function transfer(address to, uint256 value) external returns (bool success);
}

contract Erc20TransferContract
{
    address private _creditcoinErc20;

    event Erc20Transfer(address indexed from, address to, uint256 value, string indexed sighash);

    constructor(address creditcoinErc20) public
    {
        _creditcoinErc20 = creditcoinErc20;
    }

    function transfer(address to, uint256 value, string memory sighash) public returns (bool success)
    {
        require(bytes(sighash).length == 60, "Invalid sighash length");
        CreditcoinErc20 creditcoinErc20 = CreditcoinErc20(_creditcoinErc20);
        success = creditcoinErc20.transfer(to, value);
        emit Erc20Transfer(msg.sender, to, value, sighash);
    }
}
