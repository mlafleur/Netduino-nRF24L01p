This is an example app using a Netduino 2 with an nRF24L01+ radio. 

The example registeres the nRF24L01p with the address F5F0F0F0F2 and transmits a small message to another nRF24L01p at F0F0F0F0E1. 
The pin configuration is as follows:

 nRF24   ->  Netduino 2  Purpose
 --------    ----------  --------------
 1 (GND) ->  GND         Ground
 2 (VCC) ->  3V3         3v Power
 3 (CE)  ->  D9          Chip Enable
 4 (CSN) ->  D10         Chip Select
 5 (SCK) ->  D13         SPI Serial Clock (SCLK)
 6 (MOSI)->  D11         SPI Transmit (MOSI)
 7 (MISO)->  D12         SPI Recieve (MISO)
 8 (IRQ) ->  D8          Interupt              

NOTE: This is part of a larger set of examples of communicating between Netduino, Arduino and Raspberry Pi using the nRF24L01+. It is very much a work in progress. 
