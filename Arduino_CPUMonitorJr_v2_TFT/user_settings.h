#define DEBUG_IS_ON                                                       false             // show debug info to Serial monitor windows

#define UDP_PORT                                                          44445             // UDP port used to let your computer know your esp32's ip address so your computer can establish a websocket link
#define UDP_BROADCAST_ADDRESS                                             "255.255.255.255" // UDP broadcast address
                                                                                            // use "255.255.255.255" if the computer being monitored has a dynamic IP address 
                                                                                            // use the IP address of the computer being monitored if it has a static IP address
																							// note: not all networks support broadcasting to all devices, if this is your case please set your computer to use a static
																							//       IP address and use that address in this field

#define SHOW_WIFI_CONNECTING_STATUS                                       true              // show connecting status 
#define WIF_CONNECTING_STATUS_COLOUR                                      TFT_GREEN         // if SHOW_WIFI_CONNECTING_STATUS is true: colour of text on display during WIFI connection process

#define SHOW_COMPUTER_NAME                                                true              // if true show computer name, if false don't show computer name

#define SHOW_IP_ADDRESS                                                   true              // if true show an IP Address, if false don't show an IP Address
#define SHOW_LAN_ADDESS                                                   true              // if SHOW_IP_ADDRESS is true: if true show a LAN address, if false show an external address

#define SHOW_TEMPERATURE                                                  true              // if true show temperature, if false do not show temperature
#define SHOW_AVERAGE_TEMPERATURE                                          true              // if SHOW_TEMPERATURE is true: if true will show average temperature; if false will show maximum temperature
#define SHOW_TEMPERATURE_IN_CELSIUS                                       true              // if SHOW_TEMPERATURE is true: if true will show in celcius; if false will show in farenheit
#define TEMPERATURE_COLOUR                                                TFT_PURPLE        // 

#define SHOW_PERCENT_OF_MEMORY_USED                                       true              // if true show the percent of memory used; if false do not show the percent of memory used
#define MEMORY_COLOUR                                                     TFT_BLUE          // 

#define SHOW_TIME                                                         true              // if true show the time; if false do not show the time
#define SHOW_TIME_IN_TWELVE_HOUR_FORMAT                                   true              // if SHOW_TIME is true; set to true for 24 hour time format, set to false for 12 hour time format
#define SHOW_AM_PM_INDICATOR                                              true              // if SHOW_TIME is true; if SHOW_TIME_IN_TWELVE_HOUR_FORMAT is true;  if true show the am/pm indicator, if false do not show the am/pm indicator
#define SHOW_SECONDS                                                      true              // if SHOW_TIME is true; if true show the seconds, if false do not show the seconds
#define TIME_COLOUR                                                       0x0967            // turquoise       //rgb colour picker: https://chrishewett.com/blog/true-rgb565-colour-picker/      

#define SHOW_DATE                                                         true              // if true show the date; if false do not show the date
#define DATE_COLOUR                                                       0xC260            // pumpkin orange   

#define GRAPH_FRAME_COLOUR                                                TFT_WHITE         // 
#define GRAPH_GRIDLINE_COLOUR                                             TFT_DARKGREY      // 

#define SHOW_HISTORICAL_LINE_GRAPH                                        true              // if true the historical line graph will be SHOWed, if false the historical line graph will not be SHOWed
#define HISTORICAL_LINE_GRAPH_COLOUR                                      TFT_RED           // colour of the historical line graph
 
#define SHOW_CURRENT_CPU_BAR_GRAPHS                                       true              // if true the current cpu bar graphs will be SHOWed, if false  the current cpu bar graphs will not be SHOWed
#define CURRENT_CPU_BAR_GRAPHS_COLOUR                                     0x8F90            // colour of the cpu bar graphs

#define TIME_SERVER                                                      "pool.ntp.org"     // ntp time server
#define TIME_ZONE                                                        -5                 // hour offset from GMT
#define DAYLIGHT_SAVINGS_TIME                                             1                 // hour offset for Daylight Savings time (0, .5, 1)
#define UPDATE_TIME_FROM_CONNECTED_COMPUTER                               false             // if true get the time and date from the connected computer when available, if false the time will be set from the NTP Server at startup only

#define MAX_NUMBER_OF_PROCESSORS                                          96                // maximum number of CPUs in host computer (more than 96 CPUs this will require program changes)

#define SERIAL_MONITOR_SPEED                                              115200            // serial monitor speed

#define DEVICE_WILL_RESET_AFTER_THIS_MANY_SECONDS_WITH_NO_WIFI_CONNECTION 600               // if wifi connections is unavailable for this period of time; attempt to reset the wifi connection
