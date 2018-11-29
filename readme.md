https://www.gluwa.com/creditcoin

# Gluwa Creditcoin

## What is Creditcoin?
---------------------

Creditcoin is a network that enables cross-blockchain credit transaction and credit history building. Creditcoin uses blockchain technology to ensure the objectivity of its credit transaction history: each transaction on the network is distributed and verified by the network.

The Creditcoin protocol was created by Gluwa. Gluwa Creditcoin is the official implementation of the Creditcoin protocol by Gluwa.

For more information, see https://www.gluwa.com/creditcoin, or read the original whitepaper.

## License
-----------

Creditcoin is licensed under the [GNU Lesser General Public License](COPYING.LESSER) software license.

Licenses of dependencies distributed with this repository are provided under the Sawtooth\DependencyLicense directory.


## Prerequisite for Windows
------------------------

#### Boost 1.67.0 source

Place the source of boost 1.67.0 to `C:\local\boost_1_67_0`.

If you would like to use your own directory, you can change the setting in the project properties under
C/C++ => Generals => Additional Include Directories.

### Static Library 

Place the following `.lib` into `\SDK\lib\Debug` folder.  

#### Boost 1.67.0:

1. Download pre-build binaries for boost 1.67.0  

2. Take the following the `.libs` from the lib64-msvc-14.1 folder

    `libboost_chrono-vc141-mt-gd-x64-1_67.lib`  
`libboost_date_time-vc141-mt-gd-x64-1_67.lib`  
`libboost_regex-vc141-mt-gd-x64-1_67.lib`  
`libboost_system-vc141-mt-gd-x64-1_67.lib`  
`libboost_thread-vc141-mt-gd-x64-1_67.lib`


#### Cryptopp:

1. Download and install vcpkg


    `git clone https://github.com/Microsoft/vcpkg.git`  
    `vcpkg`  
    `cd vcpkg`  
    `bootstrap-vcpkg.bat`  


2. Install Cryptopp

    `.\vcpkg istall cryptopp:x64-windows`
 
    The `.lib` file will be in vcpkg\installed\x64-windows\debug\lib\cryptopp-static.lib 


#### cpp-netlib 0.13.rc1:

1. clone cpp-netlib 0.13.rc1 from git branch release 0.13 

    `git submodule init`  
    `git submodule update`  

2. Follow the instruction on https://cpp-netlib.org/0.13.0/getting_started.html

    When calling cmake, use the following command instead:

```
cmake -DCMAKE_BUILD_TYPE=Debug \  
      -DCMAKE_C_COMPILER=gcc   \  
      -DCMAKE_CXX_COMPILER=g++ \  
      -DCMAKE_GENERATOR_PLATFORM=x64 \  
      ../cpp-netlib  
```

3. Edit CPP-NETLIB.sln

    In properties => C/C++ => Preprocessor => Preprocessor Definitions:

    Remove `WIN32` for x64 platform for the following projects:  
cppnetlib-client-connections  
cppnetlib-server-parsers  
cppnetlib-uri

    build above project with x64 platform, and take the `.lib` files


#### Sawtooth:

Follow the instructions in `\Sawtooth\SDK\sawtooth.lib rebuild instructions.txt` to build the sawtooth.lib file


## Prerequisite for Ubuntu 16.04
-----------------------------

### Visual Studio's Workload for Linux development with C++

https://blogs.msdn.microsoft.com/vcblog/2017/04/11/linux-development-with-c-in-visual-studio/ 

### Ubuntu 16.04 vm.


1. The following dependencies need to be installed via apt-get to the Ubuntu 16.04 vm

    `gcc`  
`g++`  
`build-essential`  
`cmake`  
`gdbserver`  
`autoconf`  
`automake`  
`libtool`  
`curl`  
`make`  
`unzip`  
`pkg-config`  
`uuid-dev`  
`openssh-server`  
`gdb`  
`libapr1-dev`  
`libaprutil1-dev`  
`python-dev`  
`python3-dev`  
`zip` 


2. Acquire the source code of following dependencies, then build and install them on the Ubuntu 16.04 vm:

    `Boost 1.67.0`  
`Cpp-netlib 0.13.rc1`  
`protobuf 3.5.1`  
`zeromq 3.2.5`  
`zeromqpp 4.2.0`  
`log4cxx - master`  
`Cryptopp 7.0.0`  

3. Sawtooth SDK CXX - master 

    Acquire source and make the following changes:  

    in src/message_stream.cpp on line 61  
Replace  
`message.add(msg_data.data(), msg_data.length());`  
With  
`message.add(msg_data.data());`

    Build and install on the Ubuntu 16.04 vm

4. Upload headers in Sawtooth\SDK and Sawtooth\xtern to /usr/local/include on the Ubuntu 16.04 vm

### Building the processsor

To create the Ubuntu 16.04 ccprocessor, build the ccprocessorLinux project
