﻿using System.Net;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MediaCoverTests
{
    [TestFixture]
    public class CoverAlreadyExistsSpecificationFixture : CoreTest<CoverAlreadyExistsSpecification>
    {
        private HttpResponse _httpResponse;

        [SetUp]
        public void Setup()
        {
            _httpResponse = new HttpResponse(null, new HttpHeader(), "", HttpStatusCode.OK);
            Mocker.GetMock<IDiskProvider>().Setup(c => c.GetFileSize(It.IsAny<string>())).Returns(100);
            Mocker.GetMock<IHttpClient>().Setup(c => c.Head(It.IsAny<HttpRequest>())).Returns(_httpResponse);
        }

        private void GivenFileExistsOnDisk()
        {
            Mocker.GetMock<IDiskProvider>().Setup(c => c.FileExists(It.IsAny<string>())).Returns(true);
        }

        private void GivenExistingFileSize(long bytes)
        {
            GivenFileExistsOnDisk();
            Mocker.GetMock<IDiskProvider>().Setup(c => c.GetFileSize(It.IsAny<string>())).Returns(bytes);
        }
        
        private void GivenImageFileCorrupt(bool corrupt)
        {
            GivenFileExistsOnDisk();
            Mocker.GetMock<CoverAlreadyExistsSpecification>()
                  .Setup(c => c.IsValidGDIPlusImage(It.IsAny<string>()))
                  .Returns(!corrupt);
        }

        [Test]
        public void should_return_false_if_file_not_exists()
        {
            Subject.AlreadyExists("http://url", "c:\\file.exe").Should().BeFalse();
        }

        [Test]
        public void should_return_false_if_file_exists_but_different_size()
        {
            GivenExistingFileSize(100);
            _httpResponse.Headers.ContentLength = 200;
            Subject.AlreadyExists("http://url", "c:\\file.exe").Should().BeFalse();
        }

        [Test]
        public void should_return_false_if_file_exists_and_same_size_but_corrupt()
        {
            GivenExistingFileSize(100);
            GivenImageFileCorrupt(true);
            _httpResponse.Headers.ContentLength = 100;
            Subject.AlreadyExists("http://url", "c:\\file.exe").Should().BeFalse();
        }
        
        [Test]
        public void should_return_true_if_file_exists_and_same_size_and_not_corrupt()
        {
            GivenExistingFileSize(100);
            GivenImageFileCorrupt(false);
            _httpResponse.Headers.ContentLength = 100;
            Subject.AlreadyExists("http://url", "c:\\file.exe").Should().BeTrue();
        }
    }
}
