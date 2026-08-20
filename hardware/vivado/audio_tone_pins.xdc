# Pmod I2S2 on KV260 carrier card connector J2 — tone-out (CS4344 DAC) path.
#
# The DAC (line-out) sits on the Pmod's TOP ROW (J2 physical pins 1-4):
#   J2.1 = D/A MCLK, J2.2 = D/A LRCK, J2.3 = D/A SCLK, J2.4 = D/A SDIN.
# (Digilent Pmod I2S2 reference manual.) The ADC bottom row (J2.7-10) is
# unused for tone-out.
#
# J2 top-row pins 1-4 wire to carrier nets HDA11..HDA14, which map to the
# K26 SOM package pins below (cross-checked against the Xilinx Kria-PYNQ
# base.xdc and kv260_som part0_pins.xml):
#   J2.1 HDA11 -> H12   J2.2 HDA12 -> E10   J2.3 HDA13 -> D10   J2.4 HDA14 -> C11
# Bank 45 VCCO = 3.3 V, so LVCMOS33.

set_property -dict {PACKAGE_PIN H12 IOSTANDARD LVCMOS33} [get_ports mclk]   ;# J2.1 HDA11
set_property -dict {PACKAGE_PIN E10 IOSTANDARD LVCMOS33} [get_ports lrclk]  ;# J2.2 HDA12
set_property -dict {PACKAGE_PIN D10 IOSTANDARD LVCMOS33} [get_ports sclk]   ;# J2.3 HDA13
set_property -dict {PACKAGE_PIN C11 IOSTANDARD LVCMOS33} [get_ports sdin]   ;# J2.4 HDA14
