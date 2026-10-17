namespace SmarTTY.Ssh;

public delegate void DataReceivedHandler(object sender, byte[] buffer, int offset, int count, int dataType);
