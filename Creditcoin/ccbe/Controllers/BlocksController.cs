using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace ccbe.Controllers
{
    /// <summary>
    /// Blocks entries
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    public class BlocksController : Controller
    {
        private const int paginationLimit = 100;

        // GET api/blocks
        /// <summary>A segment of blocks</summary>
        /// <param name="last">An ID of a block to skip up to, normally is not set manually</param>
        /// <param name="limit">The maximum number of blocks to return - greater than 0 and less than 100</param>
        /// <returns>A new Pagination object</returns>
        /// <response code="200">If succeeds</response>
        /// <response code="400">If either parameter is invalid</response>
        /// <response code="503">If unable to access Creditcoin network</response>
        [HttpGet]
        [ProducesResponseType(200)]
        [ProducesResponseType(400)]
        [ProducesResponseType(503)]
        public IActionResult Get(string last, int? limit)
        {
            if (limit == null)
                limit = paginationLimit;
            else if (limit <= 0 || limit > paginationLimit)
                return BadRequest();

            if (!Cache.IsSuccessful())
                return new StatusCodeResult(503);

            var blocks = Cache.GetBlocks(last, limit.Value);
            if (blocks == null)
                return BadRequest();

            string next = "";
            if (blocks.Count == limit.Value)
            {
                var element = blocks[blocks.Count - 1];
                next = $"{this.Request.Scheme}://{this.Request.Host.ToUriComponent()}{this.Request.PathBase.ToUriComponent()}{this.Request.Path.ToUriComponent()}?last={element.Key}";
            }

            var pagination = new Models.Pagination { Data = blocks, Next = next };
            return Json(pagination);
        }

        // GET api/blocks/<id>
        /// <summary>The identified block</summary>
        /// <param name="id">A block ID - 128 hexadecimal digits</param>
        /// <returns>A new Block object</returns>
        /// <response code="200">If succeeds</response>
        /// <response code="400">If the parameter is invalid</response>
        /// <response code="404">If block doesn't exist</response>
        /// <response code="503">If unable to access Creditcoin network</response>
        [HttpGet("{id}")]
        [ProducesResponseType(200)]
        [ProducesResponseType(400)]
        [ProducesResponseType(404)]
        [ProducesResponseType(503)]
        public IActionResult Get(string id)
        {
            if (id.Length != 128 || !id.All("1234567890abcdef".Contains))
                return BadRequest();

            Models.Block block = Cache.GetBlock(id);
            if (block == null)
            {
                if (!Cache.IsSuccessful())
                    return new StatusCodeResult(503);
                block = Cache.GetBlock(id);
                if (block == null)
                    return NotFound();
            }
            return Json(block);
        }

        // GET api/blocks/forTxid/<txid>
        /// <summary>A block that contain the identified transaction</summary>
        /// <param name="txid">An transaction ID - 128 hexadecimal digits</param>
        /// <returns>A new mapping of the block ID to the corresponding block</returns>
        /// <response code="200">If succeeds</response>
        /// <response code="400">If the parameter is invalid</response>
        /// <response code="404">If the transaction doesn't exist</response>
        /// <response code="503">If unable to access Creditcoin network</response>
        [HttpGet("forTxid/{txid}")]
        [ProducesResponseType(200)]
        [ProducesResponseType(400)]
        [ProducesResponseType(404)]
        [ProducesResponseType(503)]
        public IActionResult GetForTxid(string txid)
        {
            if (txid.Length != 128 || !txid.All("1234567890abcdef".Contains))
                return BadRequest();

            Dictionary<string, Models.Block> blocks = Cache.findBlockWithTx(txid);
            Debug.Assert(blocks == null || blocks != null && blocks.Count == 1);
            if (blocks == null)
            {
                if (!Cache.IsSuccessful())
                    return new StatusCodeResult(503);
                blocks = Cache.findBlockWithTx(txid);
                if (blocks == null)
                    return NotFound();
            }
            return Json(blocks);
        }

        // GET api/blocks/forSighash/<sighash>
        /// <summary>A segment of blocks that contain transactions originated from the address identified by the given sighash</summary>
        /// <param name="sighash">An originator's sighash - 60 hexadecimal digits</param>
        /// <param name="last">An ID of a block to skip up to, normally is not set manually</param>
        /// <param name="limit">A maximal number of blocks to return - greater than 0 and less than 100</param>
        /// <returns>A new Pagination object</returns>
        /// <response code="200">If succeeds</response>
        /// <response code="400">If either parameter is invalid</response>
        /// <response code="503">If unable to access Creditcoin network</response>
        [HttpGet("forSighash/{sighash}")]
        [ProducesResponseType(200)]
        [ProducesResponseType(400)]
        [ProducesResponseType(503)]
        public IActionResult GetForSighash(string sighash, string last, int? limit)
        {
            if (sighash.Length != 60 || !sighash.All("1234567890abcdef".Contains))
                return BadRequest();

            if (limit == null)
                limit = paginationLimit;
            else if (limit <= 0 || limit > paginationLimit)
                return BadRequest();

            if (!Cache.IsSuccessful())
                return new StatusCodeResult(503);

            var blocks = Cache.findBlocksForSighash(sighash, last, limit.Value);
            if (blocks == null)
                return BadRequest();

            string next = "";
            if (blocks.Count == limit.Value)
            {
                var element = blocks[blocks.Count - 1];
                next = $"{this.Request.Scheme}://{this.Request.Host.ToUriComponent()}{this.Request.PathBase.ToUriComponent()}{this.Request.Path.ToUriComponent()}?last={element.Key}";
            }

            var pagination = new Models.Pagination { Data = blocks, Next = next };
            return Json(pagination);
        }
    }
}
