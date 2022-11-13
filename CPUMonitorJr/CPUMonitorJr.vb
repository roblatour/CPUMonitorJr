' Copyrigth Rob Latour, 2022

Imports System.Threading
Imports System.Timers
Imports System.Management
Imports System.Net
Imports System.Net.Sockets
Imports System.Runtime.CompilerServices
Imports Microsoft.VisualBasic.Devices
Imports System.Data.Common
Imports System.Runtime.Remoting.Lifetime
Imports OpenHardwareMonitor.Hardware
Imports GetCoreTempInfoNET


' will autostart after install ref: https://www.aspsnippets.com/Articles/Start-Windows-Service-Automatically-start-after-Installation-in-C-and-VBNet.aspx#:~:text=We%20can%20start%20the%20Windows,to%20start%20the%20Windows%20Service.

Public Class CPUMonitorJr

    '  the frequency at which data is sent to the esp32 is controlled in the setting variable My.Settings.Interval
    '  on the computer side this variable can be changed by editing in the file 'CPUMonitorJr.exe.config' which is found in the same directory as the program 'CPUMonitorJr.exe'
    '  changing the value requires the CPUMonitorJr service to be stopped and restarted
    '  the value represents the number of milli seconds between update
    '  for example, 1000 means 1 second between updates
    '  default value is 1000, an number below 200  (1/5th of a second) doesn't work out well as it drives too much overhead
    '  therefore the minimal value you can set is 100; however a value of 1000 seems to give good results

    ' the computer running this program and the ESP32 running the CPUMonitorJr sketch both must be communicating via the same UDP Port
    ' on the computer side this variable can be changed by editing in the file 'CPUMonitorJr.exe.config' which is found in the same directory as the program 'CPUMonitorJr.exe'
    ' changing the value requires the CPUMonitorJr service to be stopped and restarted
    ' on the ESP32 side this variable can be changed in the 'user_settings.h' file - and requires the sketch to be recompiled and uploaded to the ESP32

    Private gUDPListenPort As Integer

    ' the timer controls the sending of information to the ESP32;
    ' it is started and stopped depending on if the web socket is open or not
    ' in this way reporting data is not transmitted over the network unless the ESP32 is connected

    Friend WithEvents Timer As System.Timers.Timer

    ' the following controls debug logging 

    Private Const gDebugOn As Boolean = True ' manually create the folder "c:\temp" before running this service with DebugOn = True
    Private Const gDebugFilename As String = "C:\temp\CPUMonitorJr_debug.txt"

    ' the following is used to get the external IP address of the computer running this service

    Private Const gExternalIPIdentificationService As String = "https://api.ipify.org"

    ' used by Open Hardware Monitor to get temperature readings 

    Private updateVisitor As OpenHardwareMonitor.UpdateVisitor = New OpenHardwareMonitor.UpdateVisitor()
    Private computer As Global.OpenHardwareMonitor.Hardware.Computer = New Global.OpenHardwareMonitor.Hardware.Computer()

    ' used by Core Temp to get temperature readings 

    Friend CTInfo As New CoreTempInfo

    ' used for communication between the computer and the ESP32

    Private gWebSocket As WebSocket4Net.WebSocket
    Private gESP32IPAddress As String = ""       ' the ESP32's IP address will be automatically updated here when the ESP32 connects; it is used to open the web socket

    ' some misc. stuff

    Private Const gMaxCPUCoresSupported = 48

    Private Const gFiveNinths As Single = 5 / 9  ' used to convert Fahrenheit to Celsius

    Private gSendTheTime As Boolean = True
    Private gSendTheComputerNameAndIPAddresses As Boolean = True

    Protected Overrides Sub OnStart(ByVal args() As String)

        Try

            If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Starting up" & vbCrLf, True)

            ' initialize Open Hardware Manager
            computer.Open()
            computer.CPUEnabled = True
            computer.Accept(updateVisitor)

            ' establish a timer which will be used to trigger sending data to the ESP32
            ' however, don't start it now; rather it will be started once the ESP32 identifies itself later on

            Timer = New System.Timers.Timer()
            Timer.Enabled = False
            Dim Interval As Integer = Math.Max(My.Settings.Interval, 200) ' determine how frequentely data should be sent to the ESP32, minimum is once every 200 milliseconds
            Timer.Interval = Interval
            AddHandler Timer.Elapsed, AddressOf Timer_Elapsed

            If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Interval = " & Interval & vbCrLf, True)

            gUDPListenPort = CInt(My.Settings.UDPPort.Trim)

            Try
                ' send a boardcast asking ESP32 to identify itself

                AddHandler Broadcast.DataArrival1, AddressOf UDPTrigger
                Broadcast.UDP_Listen1(gUDPListenPort)
                If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Listening on UDP port " & gUDPListenPort.ToString & vbCrLf, True)

            Catch ex As Exception

                If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Can not listen on port" & vbCrLf & ex.Message.ToString & vbCrLf, True)

            End Try

        Catch ex As Exception

            My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & ex.ToString & vbCrLf, True)

        End Try

        If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Startup complete" & vbCrLf, True)

    End Sub

    Protected Overrides Sub OnStop()

        Timer.Stop()

        computer.Close()

        If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Closing UDP listening port" & vbCrLf, True)
        modBroadcast.Broadcast.CloseSock1()

    End Sub

    Friend Sub UDPTrigger(ByVal IncomingData As String)

        If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "UDPTrigger: " & IncomingData & vbCrLf, True)

        Static LastMessageReceived As String = String.Empty
        Static LastMessageReceivedTimeStamp As DateTime = Now.AddDays(-1)

        'ignor duplicate messages within five seconds of each other

        If LastMessageReceived = IncomingData Then

            If LastMessageReceivedTimeStamp.AddSeconds(5) > Now Then

                LastMessageReceivedTimeStamp = Now
                Exit Sub

            End If

        Else

            LastMessageReceived = IncomingData

        End If

        LastMessageReceivedTimeStamp = Now

        If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Incoming udp broadcast: " & IncomingData & vbCrLf, True)

        If IncomingData.StartsWith("CPUMonitorJr") Then

            Dim ws() As String = IncomingData.Split(";")
            gESP32IPAddress = ws(1).Trim

            If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "ESP32 address set to " & gESP32IPAddress & vbCrLf, True)

            If gWebSocket IsNot Nothing Then
                If gWebSocket.State <> WebSocket4Net.WebSocketState.Closed Then
                    CloseSocket()
                End If
            End If

            OpenSocket()

        End If

    End Sub

    Friend Sub OpenSocket()

        Try

            If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Opening websocket" & vbCrLf, True)

            Dim WebSocketAddress As String = "ws://" & gESP32IPAddress & "/cpumonitorjr" & gUDPListenPort

            If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "WebSocket Address = " & WebSocketAddress & vbCrLf, True)

            gWebSocket = New WebSocket4Net.WebSocket(WebSocketAddress)

            AddHandler gWebSocket.Opened, Sub(s, e) socketOpened(s, e)
            AddHandler gWebSocket.Error, Sub(s, e) socketError(s, e)
            AddHandler gWebSocket.Closed, Sub(s, e) socketClosed(s, e)
            AddHandler gWebSocket.MessageReceived, Sub(s, e) socketMessage(s, e)
            AddHandler gWebSocket.DataReceived, Sub(s, e) socketDataReceived(s, e)

            gWebSocket.Open()

            If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Websocket open" & vbCrLf, True)

        Catch ex As Exception

            If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Websocket open failed" & vbCrLf & ex.Message.ToString & vbCrLf, True)

        End Try


    End Sub

    Friend Sub CloseSocket()

        If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Closing websocket" & vbCrLf, True)

        Try

            RemoveHandler gWebSocket.Opened, Sub(s, e) socketOpened(s, e)
            RemoveHandler gWebSocket.Error, Sub(s, e) socketError(s, e)
            RemoveHandler gWebSocket.Closed, Sub(s, e) socketClosed(s, e)
            RemoveHandler gWebSocket.MessageReceived, Sub(s, e) socketMessage(s, e)
            RemoveHandler gWebSocket.DataReceived, Sub(s, e) socketDataReceived(s, e)

            gWebSocket.Close()

            If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Websocket closed" & vbCrLf, True)

        Catch ex As Exception

            If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Websocket close failed" & vbCrLf & ex.Message.ToString & vbCrLf, True)

        End Try

    End Sub

    Sub socketOpened(ByVal s As Object, ByVal e As EventArgs)

        ' each time the web socket is opened, send the time, computer name, and IP Addresses
        gSendTheTime = True
        gSendTheComputerNameAndIPAddresses = True
        Timer.Enabled = True
        Timer.Start()

    End Sub

    Sub socketClosed(ByVal s As Object, ByVal e As EventArgs)

        Timer.Stop()

    End Sub

    Sub socketError(ByVal s As Object, ByVal e As SuperSocket.ClientEngine.ErrorEventArgs)

    End Sub

    Sub socketMessage(ByVal s As Object, ByVal e As WebSocket4Net.MessageReceivedEventArgs)

    End Sub
    Sub socketDataReceived(ByVal ss As Object, ByVal e As WebSocket4Net.DataReceivedEventArgs)

    End Sub

    Private synlockObject As New Object

    Private Sub Timer_Elapsed(sender As Object, e As ElapsedEventArgs) Handles Timer.Elapsed

        ' for this timer tick

        '    when the gSendTheTime flag is set send the time data
        '    also send the time data approximately 24 hours after the last time the time data was sent
        '    this will help keep the time displayed on the esp32 in sync with the computer's time

        '    when the gSendTheComputerNameAndIPAddresses flag is set send the computer name and ip addresss 

        '    if neither the gSendTheTime nor gSendTheComputerNameAndIPAddresses flag are set, send the memory, temperature and CPU readings

        Static Dim TimeSinceTimeWasLastSent = 0
        Const delayAfterSendingTheTimeOrComputerNameAndIPAddresses = 1000
        Const TwentyFourHours As Integer = 24 * 60 * 60 * 1000

        Dim sw As New Stopwatch
        sw.Start()

        SyncLock synlockObject

            Try

                Dim SendTheMemoryTemperatureAndCPUReadings As Boolean = Not (gSendTheTime OrElse gSendTheComputerNameAndIPAddresses)

                If gSendTheTime OrElse (TimeSinceTimeWasLastSent > TwentyFourHours) Then

                    SendTime()
                    TimeSinceTimeWasLastSent = 0
                    Thread.Sleep(delayAfterSendingTheTimeOrComputerNameAndIPAddresses)

                End If

                If gSendTheComputerNameAndIPAddresses Then

                    SendComputerNameAndIPAddresss()
                    Thread.Sleep(delayAfterSendingTheTimeOrComputerNameAndIPAddresses)

                End If

                If SendTheMemoryTemperatureAndCPUReadings Then
                    SendMemoryTemperatureAndCPUReadings()
                End If

            Catch ex As Exception
                If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & ex.ToString & vbCrLf, True)
            End Try

        End SyncLock

        sw.Stop()

        TimeSinceTimeWasLastSent += Timer.Interval + sw.ElapsedMilliseconds

    End Sub

    Private Sub SendTime()

        ' byte 0 = is a '0' to represent this is a time stream 
        ' byte 1 = year  (current year - 2020) ' warning this will need to change for the year 2275 :-)
        ' byte 2 = month
        ' byte 3 = day
        ' byte 4 = dayofweek
        ' byte 5 = hour
        ' byte 6 = minute
        ' byte 7 = second

        Try

            Dim sendArray(9) As Byte

            Dim RightNow As Date = Now()

            sendArray(0) = 0
            sendArray(1) = RightNow.Year - 2000
            sendArray(2) = RightNow.Month
            sendArray(3) = RightNow.Day
            sendArray(4) = RightNow.DayOfWeek
            sendArray(5) = RightNow.Hour
            sendArray(6) = RightNow.Minute
            sendArray(7) = RightNow.Second

            If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "sending time" & vbCrLf, True)

            gWebSocket.Send(sendArray, 0, sendArray.Length)

            gSendTheTime = False


        Catch ex As Exception
            If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & ex.ToString & vbCrLf, True)
        End Try

    End Sub

    Private Sub SendComputerNameAndIPAddresss()

        ' byte 0                  = is a '1' to represent this is a Computer name and IP address stream
        ' byte 1 ... byte n       = Computer name
        ' byte n + 1              = ';'
        ' byte n + 2 ... byte m   = LAN IP Address 
        ' byte m + 1              = ';'
        ' byte m + 2 ... byte z   = External IP Address 
        ' final byte              = ';'

        Try

            Dim Computer_Name As String = Dns.GetHostName()
            Dim LAN_Address As String = ""
            Dim External_Address As String = ""

            ' get LAN address
            Dim host As IPHostEntry = Dns.GetHostEntry(Computer_Name)
            For Each ip As IPAddress In host.AddressList

                If ip.AddressFamily = AddressFamily.InterNetwork Then
                    LAN_Address = ip.ToString
                    Exit For
                End If

            Next

            ' get External Address
            Try
                External_Address = New System.Net.WebClient().DownloadString(gExternalIPIdentificationService).Trim
            Catch ex As Exception
            End Try

            If (External_Address.Length = 0) Then External_Address = "External address not available"

            Dim ComputerNameAndIPAddresses = Computer_Name & ";" & LAN_Address & ";" & External_Address & ";"

            Dim sendArray() As Byte = System.Text.Encoding.ASCII.GetBytes("_" & ComputerNameAndIPAddresses)

            sendArray(0) = 1
            If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "sending: " & ComputerNameAndIPAddresses & vbCrLf, True)

            gWebSocket.Send(sendArray, 0, sendArray.Length)

            gSendTheComputerNameAndIPAddresses = False

        Catch ex As Exception
            If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & ex.ToString & vbCrLf, True)
        End Try

    End Sub

    Private Sub SendMemoryTemperatureAndCPUReadings()

        Try
            ' send data

            ' byte 0 = is a '2' to represent this is a temperature and cpu data stream 
            ' byte 1 = percent of memory used whole number
            ' byte 2 = percent of memory used decimal
            ' byte 3 = average temp whole number
            ' byte 4 = average temp decimal
            ' byte 3 = max temp whole number
            ' byte 5 = max temp decimal
            ' byte 7 = number of cpus
            ' byte 8 and on  = cpu busy of each cpu

            Dim tempArray(6) As Byte

            ' in the tempArray:
            '    byte 0 will be used for the whole number part of the percent of memory used
            '    byte 1 will be used for the decimal part of the percent of memory used
            '    byte 2 will be used for the whole number part of the average temperture
            '    byte 3 will be used for the decimal part of the average tempeture
            '    byte 4 will be used for the whole number part of the max temperture
            '    byte 5 will be used for the decimal part of the max tempeture

            Dim CPUValues(gMaxCPUCoresSupported - 1) As Double

            Dim numberOfCores As Integer

            Dim wholeNumber, decimalNumber As Integer

            Dim TemperatureToOneDecimalPlace As Single

            ' get the percent of memory used from Windows (its not available via Core Temp)

            Dim PercentOfMemoryUsed As Single = (1 - (My.Computer.Info.AvailablePhysicalMemory / My.Computer.Info.TotalPhysicalMemory)) * 100
            Dim PercentOfMemoryUsedToOneDecimalPlace As Single = Math.Round(PercentOfMemoryUsed, 1, MidpointRounding.AwayFromZero)

            ' load the percent of memory used

            wholeNumber = Math.Truncate(PercentOfMemoryUsedToOneDecimalPlace)
            decimalNumber = Math.Truncate(PercentOfMemoryUsedToOneDecimalPlace * 10) - wholeNumber * 10
            tempArray(0) = wholeNumber
            tempArray(1) = decimalNumber


            ' get the average and max temperatures

            ' the first approach try using the Open Hardware Monitor approach

            Dim averageTemperature As Single = 0
            Dim maximumTemperature As Single = 0

            For i As Integer = 0 To computer.Hardware.Length - 1

                If computer.Hardware(i).HardwareType = HardwareType.CPU Then

                    Dim averageFound As Boolean = False
                    Dim maxFound As Boolean = False

                    For j As Integer = 0 To computer.Hardware(i).Sensors.Length - 1

                        If computer.Hardware(i).Sensors(j).SensorType = SensorType.Temperature Then

                            If computer.Hardware(i).Sensors(j).Name.Contains("Average") Then
                                averageTemperature = computer.Hardware(i).Sensors(j).Value
                                averageFound = True
                                Exit For
                            End If

                            If computer.Hardware(i).Sensors(j).Name.Contains("Max") Then
                                maximumTemperature = computer.Hardware(i).Sensors(j).Value
                                maxFound = True
                            End If

                            If averageFound AndAlso maxFound Then
                                Exit For
                            End If

                        End If

                    Next


                End If

            Next

            ' if the Open Hardware Monitor approach did not work try the Core Temp approach  

            If (averageTemperature = 0) AndAlso (maximumTemperature = 0) Then

                Dim reading As Single
                Dim numberOfReadings As Integer = 0
                Dim totalOfAllReadings As Single = 0
                Dim maxiumReading As Single = 0

                Dim bReadSuccess As Boolean = CTInfo.GetData()

                If bReadSuccess Then

                    Dim CPUCount As UInteger = CTInfo.GetCPUCount
                    Dim CoreCount As UInteger = CTInfo.GetCoreCount

                    For i As UInteger = 0 To CPUCount - 1

                        For j As UInteger = 0 To CPUCount - 1

                            numberOfReadings += 1

                            reading = CTInfo.GetTemp(j + (i * CoreCount))

                            totalOfAllReadings += reading

                            If reading > maxiumReading Then
                                maxiumReading = reading
                            End If

                        Next

                    Next

                    If numberOfReadings > 0 Then

                        If CTInfo.IsFahrenheit Then
                            ' convert to Celcius
                            totalOfAllReadings = (totalOfAllReadings - 32) * gFiveNinths
                            maxiumReading = (maxiumReading - 32) * gFiveNinths
                        End If

                        averageTemperature = totalOfAllReadings / numberOfReadings
                        maximumTemperature = maxiumReading

                    End If

                End If

            End If

            'load the average temperature

            TemperatureToOneDecimalPlace = Math.Round(averageTemperature, 1, MidpointRounding.AwayFromZero)
            wholeNumber = Math.Truncate(TemperatureToOneDecimalPlace)
            decimalNumber = Math.Truncate(TemperatureToOneDecimalPlace * 10) - wholeNumber * 10
            tempArray(2) = wholeNumber
            tempArray(3) = decimalNumber

            'load the max temperature

            TemperatureToOneDecimalPlace = Math.Round(maximumTemperature, 1, MidpointRounding.AwayFromZero)
            wholeNumber = Math.Truncate(TemperatureToOneDecimalPlace)
            decimalNumber = Math.Truncate(TemperatureToOneDecimalPlace * 10) - wholeNumber * 10
            tempArray(4) = wholeNumber
            tempArray(5) = decimalNumber

            ' get the cpu readings 
            ' Windows performace counters are used in stead of the results also availabe from Open Hardware Manager and Core Temp as Windows performace counters report virtual CPUs where Open Hardware Manager and Core Temp only report physical cpus

            Dim pc As PerformanceCounter = New PerformanceCounter("Processor Information", "% Processor Time")
            Dim cat = New PerformanceCounterCategory("Processor Information")

            Dim instanceNames As String() = cat.GetInstanceNames

            Dim filteredInstanceNames(instanceNames.Length) As String
            Dim filteredInstancesIndex As Integer = 0

            ' filter out totals
            For Each thing In instanceNames
                If thing.EndsWith("Total") Then
                Else
                    filteredInstanceNames(filteredInstancesIndex) = thing
                    filteredInstancesIndex += 1
                End If
            Next

            ReDim Preserve filteredInstanceNames(filteredInstancesIndex - 1)
            Array.Sort(filteredInstanceNames)

            Dim cs = New Dictionary(Of String, CounterSample)
            For Each s In filteredInstanceNames
                pc.InstanceName = s
                cs.Add(s, pc.NextSample)
            Next

            numberOfCores = filteredInstanceNames.Count

            Dim index As Integer = 0

            Thread.Sleep(50)  ' this is absolutely needed, do not remove ( learned that the hard way after several days! ) 

            For Each s In filteredInstanceNames

                pc.InstanceName = s
                CPUValues(index) = calculateCPUValue(cs(s), pc.NextSample)
                cs(s) = pc.NextSample

                index += 1

            Next

            ' send the readings

            ' send data

            ' byte 0 = is a '2' to represent this is a temperature and cpu data stream 
            ' byte 1 = percent of memory used whole number
            ' byte 2 = percent of memory used decimal
            ' byte 3 = average temp whole number
            ' byte 4 = average temp decimal
            ' byte 3 = max temp whole number
            ' byte 5 = max temp decimal
            ' byte 7 = number of cpus
            ' byte 8 and on  = cpu busy of each cpu

            Dim sendArray(numberOfCores + 7) As Byte

            sendArray(0) = 2

            sendArray(1) = tempArray(0)
            sendArray(2) = tempArray(1)

            sendArray(3) = tempArray(2)
            sendArray(4) = tempArray(3)

            sendArray(5) = tempArray(4)
            sendArray(6) = tempArray(5)

            sendArray(7) = numberOfCores

            For x = 0 To numberOfCores - 1

                If CPUValues(x) > 0 Then
                    sendArray(8 + x) = Int(Math.Round(CPUValues(x)))
                Else
                    sendArray(8 + x) = 0
                End If

            Next

            gWebSocket.Send(sendArray, 0, sendArray.Length)

        Catch ex As Exception
            If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & ex.ToString & vbCrLf, True)
        End Try

    End Sub

    Public Shared Function calculateCPUValue(ByVal oldSample As CounterSample, ByVal newSample As CounterSample) As Double

        Dim difference As Double = (newSample.RawValue - oldSample.RawValue)
        Dim timeInterval As Double = (newSample.TimeStamp100nSec - oldSample.TimeStamp100nSec)
        If (timeInterval <> 0) Then
            Return (100 * (1 - (difference / timeInterval)))
        End If

        Return 0

    End Function

    Friend Function GetTemp() As Single

        Dim Result As Single = 0

        Dim avgTempKelvin As Single = 0

        Dim count As Integer = 0

        Try

            Dim searcher As New ManagementObjectSearcher("root\WMI", "SELECT * FROM MSAcpi_ThermalZoneTemperature")

            For Each queryObj As ManagementObject In searcher.Get()

                count = count + 1
                avgTempKelvin = avgTempKelvin + queryObj("CurrentTemperature")

            Next

            If count > 0 Then

                Result = avgTempKelvin / count

                ' convert from Kalvin to Celsius

                Result = Result / 10 - 273.2  ' normally this would be 273.15 however the results from the ManagementObject only returned to one decimal place

            End If

        Catch ex As Exception

        End Try

        ' If DebugOn Then My.Computer.FileSystem.WriteAllText(DebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "temperature " & Result & vbCrLf, True)

        Return Result

    End Function

End Class
Class OpenHardwareMonitor
    Public Class UpdateVisitor
        Implements IVisitor

        Public Sub VisitComputer(ByVal computer As IComputer) Implements IVisitor.VisitComputer
            computer.Traverse(Me)
        End Sub

        Public Sub VisitHardware(ByVal hardware As IHardware) Implements IVisitor.VisitHardware
            hardware.Update()

            For Each subHardware As IHardware In hardware.SubHardware
                subHardware.Accept(Me)
            Next
        End Sub
        Public Sub VisitSensor(ByVal sensor As ISensor) Implements IVisitor.VisitSensor
        End Sub

        Public Sub VisitParameter(ByVal parameter As IParameter) Implements IVisitor.VisitParameter
        End Sub
    End Class

End Class