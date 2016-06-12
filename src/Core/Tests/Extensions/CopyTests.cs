using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Xunit;

namespace Foundatio.Tests.Extensions {
    public class CopyTests {

        [Fact]
        public void CopyTest1() {


            var contact = new Tester();
            contact.Idx["date000001"] = new DateTime(2016, 6, 8);
            contact.Data["Birthday"] = new DateTime(2016, 6, 8);


            var copy = contact.Copy();


            Assert.Equal(contact.Idx["date000001"], copy.Idx["date000001"]);
            Assert.Equal(contact.Data["Birthday"], copy.Data["Birthday"]);

        }

        [Fact]
        public void CopyTest2() {


            var contact = new Tester();
            contact.Idx["date000001"] = new Root {
                User1 = new UserTest {
                    FirstName = "Blah",
                    LastName = "Wee",
                    BirthDay = new DateTime(2016, 6, 10)
                },
                User2 = new UserTest {
                    FirstName = "Blah",
                    LastName = "Wee",
                    BirthDay = new DateTime(2016, 6, 10)
                }
            };
            contact.Data["Birthday"] = new Root {
                User1 = new UserTest {
                    FirstName = "Blah",
                    LastName = "Wee",
                    BirthDay = new DateTime(2016, 6, 10)
                },
                User2 = new UserTest {
                    FirstName = "Blah",
                    LastName = "Wee",
                    BirthDay = new DateTime(2016, 6, 10)
                }
            };


            var copy = contact.Copy();

        }


    }

    public class Tester {
        public Dictionary<string, object> Idx { get; set; } = new Dictionary<string, object>();
        public Dictionary<string, object> Data { get; set; } = new Dictionary<string, object>();

    }


    public class UserTest {
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public DateTime BirthDay { get; set; }
    }

    public class Root {
        public UserTest User1 { get; set; }
        public UserTest User2 { get; set; }
    }
}
