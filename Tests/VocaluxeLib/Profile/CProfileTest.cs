#region license
// This file is part of Vocaluxe.
// 
// Vocaluxe is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// Vocaluxe is distributed in the hope that it will be useful, but
// WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with Vocaluxe. If not, see <http://www.gnu.org/licenses/>.
#endregion

using NUnit.Framework;
using VocaluxeLib.Profile;
using VocaluxeLib.Xml;

namespace Tests.VocaluxeLib.Profile
{
    [TestFixture]
    public class CProfileTest
    {
        // A profile without an avatar (e.g. no avatar images are present at all) must serialize:
        // the AvatarFileName getter used to dereference the null Avatar, so saving a new profile
        // crashed with a NullReferenceException on an unseeded Linux install. A null avatar name
        // now writes an empty <Avatar> element instead.
        [Test]
        public void CProfileSerialize_NoAvatar_DoesNotThrow()
        {
            CProfile profile = new CProfile { PlayerName = "NoAvatar" };

            string xml = new CXmlSerializer().Serialize(profile);

            StringAssert.Contains("<Avatar />", xml);
        }
    }
}