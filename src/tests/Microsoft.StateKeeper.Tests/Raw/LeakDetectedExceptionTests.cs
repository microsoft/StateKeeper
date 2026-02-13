using Microsoft.StateKeeper.Raw;

namespace Microsoft.StateKeeper.Tests.Raw;

[TestClass]
public class LeakDetectedExceptionTests
{
    [TestMethod]
    public void Constructor_WithMessage_SetsMessageProperty()
    {
        // Arrange
        const string expectedMessage = "Test leak detected message";

        // Act
        var exception = new LeakDetectedException(expectedMessage);

        // Assert
        Assert.AreEqual(expectedMessage, exception.Message);
    }

    [TestMethod]
    public void Constructor_WithMessage_IsInvalidOperationException()
    {
        // Arrange
        const string message = "Test message";

        // Act
        var exception = new LeakDetectedException(message);

        // Assert
        Assert.IsInstanceOfType<InvalidOperationException>(exception);
    }

    [TestMethod]
    public void Constructor_WithLockTypeLabelAndStackTrace_SetsFormattedMessage()
    {
        // Arrange
        const string lockTypeLabel = "AsyncLock.Releaser";
        const string acquisitionStackTrace = "   at TestMethod() in TestFile.cs:line 42";

        // Act
        var exception = new LeakDetectedException(lockTypeLabel, acquisitionStackTrace);

        // Assert
        Assert.IsTrue(exception.Message.Contains(lockTypeLabel, StringComparison.Ordinal));
        Assert.IsTrue(exception.Message.Contains(acquisitionStackTrace, StringComparison.Ordinal));
        Assert.IsTrue(exception.Message.Contains("was garbage collected without being disposed", StringComparison.Ordinal));
        Assert.IsTrue(exception.Message.Contains("Lock was acquired at:", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Constructor_WithLockTypeLabelAndStackTrace_IsInvalidOperationException()
    {
        // Arrange
        const string lockTypeLabel = "AsyncLock.Releaser";
        const string acquisitionStackTrace = "   at TestMethod()";

        // Act
        var exception = new LeakDetectedException(lockTypeLabel, acquisitionStackTrace);

        // Assert
        Assert.IsInstanceOfType<InvalidOperationException>(exception);
    }

    [TestMethod]
    public void Constructor_WithDifferentLockTypeLabels_FormatsMessageCorrectly()
    {
        // Arrange
        const string writeLockLabel = "AsyncReaderWriterLock.Releaser (write lock)";
        const string readLockLabel = "AsyncReaderWriterLock.Releaser (read lock)";
        const string stackTrace = "test stack trace";

        // Act
        var writeException = new LeakDetectedException(writeLockLabel, stackTrace);
        var readException = new LeakDetectedException(readLockLabel, stackTrace);

        // Assert
        Assert.IsTrue(writeException.Message.StartsWith(writeLockLabel, StringComparison.Ordinal));
        Assert.IsTrue(readException.Message.StartsWith(readLockLabel, StringComparison.Ordinal));
    }
}
