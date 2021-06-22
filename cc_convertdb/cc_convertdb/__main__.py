import logging
from argparse import ArgumentParser
from cc_convertdb import LMDBConverter, LOGGER

LOGGER.setLevel(logging.INFO)
LOGGER.addHandler(logging.StreamHandler())

argparser = ArgumentParser()
argparser.prog = 'cc_convertdb'
argparser.add_argument('-r', '--revert', action='store_true',
    help='Revert the file back to its original state.')
argparser.add_argument('-w', '--num_workers', type=int, default=4,
    help='Number of worker threads to read from the merkle database.')
argparser.add_argument('data_path', metavar='DATAPATH',
    help='Data directory for the database files.')
argparser.add_argument('blocknums', metavar='BLOCKNUM', nargs='*',  type=int,
    help='Block numbers for states to convert. Defaults to chain head.')
parsed = argparser.parse_args()
with LMDBConverter(parsed.data_path, parsed.revert, parsed.blocknums, parsed.num_workers) as converter:
    converter.convert_tree()