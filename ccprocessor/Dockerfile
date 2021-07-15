FROM ubuntu:xenial-20210114

WORKDIR /home/Creditcoin

COPY ./ccprocessorLinux.out /home/Creditcoin
COPY ./lib/* /home/Creditcoin/lib/

RUN apt-get update
RUN apt-get install -y libexpat1 curl