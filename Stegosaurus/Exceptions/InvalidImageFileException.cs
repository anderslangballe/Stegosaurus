﻿namespace Stegosaurus.Exceptions
{
    public class InvalidImageFileException : InvalidCarrierFileException
    {
        public InvalidImageFileException(string _message, string _fileName)
            : base($"Image file was invalid. {_message}", _fileName)
        {
        }
    }
}
